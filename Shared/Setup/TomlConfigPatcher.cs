using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AkerMcp.Shared.Setup
{
    /// <summary>
    /// The TOML half of <see cref="McpConfigPatcher"/>, for Codex (~/.codex/config.toml, servers under
    /// <c>[mcp_servers.&lt;name&gt;]</c>).
    ///
    /// Not a TOML library: a library would parse the whole file and write it back in its own layout. This edits only
    /// the lines of the one table it manages — <c>command</c> and <c>args</c> inside <c>[mcp_servers.game-engine]</c>,
    /// or the whole table when adding or removing it — and leaves every other byte alone: comments, other servers,
    /// key order, quoting, line endings. Keys the user added to the table (<c>env_vars</c>, <c>startup_timeout_sec</c>,
    /// a <c>.env</c> subtable) survive a re-apply. When the server is defined in a form this does not edit (a dotted key
    /// or an inline table), it refuses rather than guessing.
    /// </summary>
    internal static class TomlConfigPatcher
    {
        /// <summary>Codex waits 10 s by default; a freshly downloaded 70 MB server can spend that in an antivirus scan.</summary>
        private const int StartupTimeoutSeconds = 60;

        public static ClientConfigStatus Inspect(AiClientDefinition client, string serverKey)
        {
            var status = new ClientConfigStatus { FileExists = File.Exists(client.ConfigPath) };
            if (!status.FileExists) return status;
            try
            {
                var doc = TomlLines.Parse(File.ReadAllText(client.ConfigPath));
                var block = doc.FindTable(client.RootProperty, serverKey, out var elsewhere);
                if (elsewhere != null)
                {
                    status.ParseError = elsewhere;
                    return status;
                }
                if (block == null) return status;

                status.IsConfigured = true;
                var command = doc.FindKey(block.Value, "command");
                if (command != null && TomlLines.TryParseString(command.Value.ValueText, out var cmd))
                    status.Command = cmd;
                var args = doc.FindKey(block.Value, "args");
                if (args != null && TomlLines.TryParseStringArray(args.Value.ValueText, out var list))
                    status.Args = list;
            }
            catch (Exception ex)
            {
                status.ParseError = ex.Message;
            }
            return status;
        }

        public static ConfigPatchResult Configure(AiClientDefinition client, McpServerLaunchSpec spec, string serverKey, bool dryRun)
        {
            try
            {
                var exists = File.Exists(client.ConfigPath);
                var original = exists ? File.ReadAllText(client.ConfigPath) : "";
                var doc = TomlLines.Parse(original);
                var block = doc.FindTable(client.RootProperty, serverKey, out var elsewhere);
                if (elsewhere != null)
                    return Fail(client, elsewhere);

                var commandLine = "command = " + TomlLines.FormatString(spec.Command);
                var argsLine = "args = [" + string.Join(", ", spec.Args.Select(TomlLines.FormatString)) + "]";

                if (block == null)
                {
                    doc.AppendTable(new[]
                    {
                        $"[{client.RootProperty}.{TomlLines.FormatKey(serverKey)}]",
                        commandLine,
                        argsLine,
                        $"startup_timeout_sec = {StartupTimeoutSeconds}",
                    });
                }
                else
                {
                    // Args first: replacing command may shift the lines below it.
                    ReplaceOrInsert(doc, block.Value, "args", argsLine);
                    block = doc.FindTable(client.RootProperty, serverKey, out _);
                    ReplaceOrInsert(doc, block!.Value, "command", commandLine);
                }

                var updated = doc.ToText();
                if (updated == original)
                    return new ConfigPatchResult { Success = true, Changed = false, ConfigPath = client.ConfigPath, Message = $"{client.DisplayName} already starts {spec}." };

                if (dryRun)
                    return new ConfigPatchResult { Success = true, Changed = true, ConfigPath = client.ConfigPath, Message = $"[dry-run] Would set [{client.RootProperty}.{serverKey}] in {client.ConfigPath} to: {spec}" };

                var backup = McpConfigPatcher.WriteTextAtomically(client.ConfigPath, updated, exists);
                return new ConfigPatchResult { Success = true, Changed = true, ConfigPath = client.ConfigPath, BackupPath = backup, Message = $"Configured {client.DisplayName}. {client.RestartHint}" };
            }
            catch (Exception ex)
            {
                return Fail(client, ex.Message);
            }
        }

        public static ConfigPatchResult Remove(AiClientDefinition client, string serverKey, bool dryRun)
        {
            try
            {
                if (!File.Exists(client.ConfigPath))
                    return new ConfigPatchResult { Success = true, Changed = false, ConfigPath = client.ConfigPath, Message = $"{client.DisplayName} has no config file: nothing to remove." };

                var original = File.ReadAllText(client.ConfigPath);
                var doc = TomlLines.Parse(original);
                var block = doc.FindTable(client.RootProperty, serverKey, out var elsewhere);
                if (elsewhere != null) return Fail(client, elsewhere);
                if (block == null)
                    return new ConfigPatchResult { Success = true, Changed = false, ConfigPath = client.ConfigPath, Message = $"{client.DisplayName} has no '{serverKey}' entry." };

                if (dryRun)
                    return new ConfigPatchResult { Success = true, Changed = true, ConfigPath = client.ConfigPath, Message = $"[dry-run] Would remove [{client.RootProperty}.{serverKey}] from {client.ConfigPath}." };

                doc.RemoveTable(block.Value);
                var backup = McpConfigPatcher.WriteTextAtomically(client.ConfigPath, doc.ToText(), createBackup: true);
                return new ConfigPatchResult { Success = true, Changed = true, ConfigPath = client.ConfigPath, BackupPath = backup, Message = $"Removed '{serverKey}' from {client.DisplayName}. {client.RestartHint}" };
            }
            catch (Exception ex)
            {
                return Fail(client, ex.Message);
            }
        }

        private static void ReplaceOrInsert(TomlLines doc, TomlLines.Table table, string key, string line)
        {
            var existing = doc.FindKey(table, key);
            if (existing != null) doc.ReplaceLines(existing.Value.FirstLine, existing.Value.LastLine, line);
            else doc.InsertLine(table.HeaderLine + 1, line);
        }

        private static ConfigPatchResult Fail(AiClientDefinition client, string why) => new ConfigPatchResult
        {
            Success = false,
            ConfigPath = client.ConfigPath,
            Message = $"Could not update {client.DisplayName} ({client.ConfigPath}): {why}"
        };
    }

    /// <summary>A TOML file as lines, with just enough of the grammar to find tables, keys and where a value ends.</summary>
    internal sealed class TomlLines
    {
        public readonly struct Table
        {
            public Table(int headerLine, int mainEnd, int end) { HeaderLine = headerLine; MainEnd = mainEnd; End = end; }
            public int HeaderLine { get; }
            /// <summary>One past the last line of the table's own keys (where its first subtable starts, or End).</summary>
            public int MainEnd { get; }
            /// <summary>One past the last line of the table and its subtables.</summary>
            public int End { get; }
        }

        public readonly struct KeyValue
        {
            public KeyValue(int firstLine, int lastLine, string valueText) { FirstLine = firstLine; LastLine = lastLine; ValueText = valueText; }
            public int FirstLine { get; }
            public int LastLine { get; }
            public string ValueText { get; }
        }

        private readonly List<string> _lines;
        private readonly string _newline;
        private readonly bool _endsWithNewline;

        private TomlLines(List<string> lines, string newline, bool endsWithNewline)
        {
            _lines = lines;
            _newline = newline;
            _endsWithNewline = endsWithNewline;
        }

        public static TomlLines Parse(string text)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var endsWithNewline = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal);
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (endsWithNewline && lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return new TomlLines(lines, newline, endsWithNewline);
        }

        public string ToText()
        {
            var body = string.Join(_newline, _lines);
            return _endsWithNewline && _lines.Count > 0 ? body + _newline : body;
        }

        /// <summary>
        /// The <c>[root.key]</c> table, with its subtables. <paramref name="elsewhere"/> says why the file cannot be edited
        /// when root.key is also, or only, defined some other way (a dotted key, an inline table, a second header).
        /// </summary>
        public Table? FindTable(string root, string key, out string? elsewhere)
        {
            elsewhere = null;
            var target = new[] { root, key };
            Table? found = null;
            var current = new List<string>();      // the table the following key lines belong to
            var start = -1;
            var mainEnd = -1;

            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                var header = HeaderPath(line);
                if (header != null)
                {
                    var inside = StartsWith(header, target);
                    if (start >= 0 && !inside)
                    {
                        if (mainEnd < 0) mainEnd = i;
                        found = new Table(start, mainEnd, i);
                        start = -1;
                    }
                    else if (start >= 0 && inside && mainEnd < 0)
                    {
                        mainEnd = i; // first subtable of ours
                    }

                    if (header.Count == 2 && inside)
                    {
                        if (found != null || start >= 0 && i != start)
                        {
                            elsewhere = $"[{root}.{key}] appears twice; fix the file by hand.";
                            return null;
                        }
                        start = i;
                        mainEnd = -1;
                    }
                    current = header;
                    continue;
                }

                var keyPath = KeyPath(line);
                if (keyPath == null) { i = SkipValue(i); continue; }
                var full = current.Concat(keyPath).ToList();
                if (start < 0 && StartsWith(full, target) && full.Count >= 2)
                {
                    elsewhere = $"{root}.{key} is defined with a dotted key or an inline table, which AkerMCP does not edit; " +
                                $"change it by hand or write it as a [{root}.{key}] table.";
                    return null;
                }
                i = SkipValue(i);
            }

            if (start >= 0) found = new Table(start, mainEnd < 0 ? _lines.Count : mainEnd, _lines.Count);
            return found;
        }

        public KeyValue? FindKey(Table table, string key)
        {
            for (var i = table.HeaderLine + 1; i < table.MainEnd; i++)
            {
                var path = KeyPath(_lines[i]);
                if (path == null) continue;
                var last = SkipValue(i);
                if (path.Count == 1 && path[0] == key)
                {
                    var text = string.Join("\n", _lines.Skip(i).Take(last - i + 1));
                    var eq = IndexOfEquals(text);
                    return new KeyValue(i, last, StripComment(text.Substring(eq + 1)).Trim());
                }
                i = last;
            }
            return null;
        }

        public void ReplaceLines(int first, int last, string line)
        {
            _lines.RemoveRange(first, last - first + 1);
            _lines.Insert(first, line);
        }

        public void InsertLine(int index, string line) => _lines.Insert(index, line);

        public void AppendTable(IEnumerable<string> lines)
        {
            while (_lines.Count > 0 && _lines[_lines.Count - 1].Trim().Length == 0) _lines.RemoveAt(_lines.Count - 1);
            if (_lines.Count > 0) _lines.Add("");
            _lines.AddRange(lines);
        }

        public void RemoveTable(Table table)
        {
            var first = table.HeaderLine;
            var end = table.End;
            // Comments right above the next table describe that table: they stay.
            while (end > first + 1 && IsCommentOrBlank(_lines[end - 1]) && _lines[end - 1].Trim().Length > 0) end--;
            _lines.RemoveRange(first, end - first);
            // One blank line was there to separate us from what came before: do not leave two in a row.
            if (first > 0 && _lines[first - 1].Trim().Length == 0 && (first >= _lines.Count || _lines[first].Trim().Length == 0))
                _lines.RemoveAt(first - 1);
        }

        // ---- grammar ------------------------------------------------------------------------------------------

        /// <summary>The dotted path of a <c>[table]</c> header line, or null when the line is not one. <c>[[array]]</c> counts as a header too.</summary>
        private static List<string>? HeaderPath(string line)
        {
            var t = line.TrimStart();
            if (!t.StartsWith("[", StringComparison.Ordinal)) return null;
            var arrayOfTables = t.StartsWith("[[", StringComparison.Ordinal);
            var pos = arrayOfTables ? 2 : 1;
            var path = ReadDottedKey(t, ref pos);
            if (path == null) return null;
            while (pos < t.Length && t[pos] == ' ' || pos < t.Length && t[pos] == '\t') pos++;
            if (pos >= t.Length || t[pos] != ']') return null;
            if (arrayOfTables) path.Add("[]"); // never equal to a real key: an array of tables ends our table
            return path;
        }

        /// <summary>The dotted key of a <c>key = value</c> line, or null for blanks, comments and continuation lines.</summary>
        private static List<string>? KeyPath(string line)
        {
            var t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#' || t[0] == '[') return null;
            var pos = 0;
            var path = ReadDottedKey(t, ref pos);
            if (path == null) return null;
            while (pos < t.Length && (t[pos] == ' ' || t[pos] == '\t')) pos++;
            return pos < t.Length && t[pos] == '=' ? path : null;
        }

        private static List<string>? ReadDottedKey(string s, ref int pos)
        {
            var parts = new List<string>();
            while (true)
            {
                while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
                if (pos >= s.Length) return null;
                string? part;
                if (s[pos] == '"' || s[pos] == '\'')
                {
                    var end = FindStringEnd(s, pos);
                    if (end < 0 || !TryParseString(s.Substring(pos, end - pos + 1), out part)) return null;
                    pos = end + 1;
                }
                else
                {
                    var start = pos;
                    while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || s[pos] == '-')) pos++;
                    if (pos == start) return null;
                    part = s.Substring(start, pos - start);
                }
                parts.Add(part!);
                while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
                if (pos < s.Length && s[pos] == '.') { pos++; continue; }
                return parts;
            }
        }

        /// <summary>The last line of the value that starts on line <paramref name="i"/>: arrays and multi-line strings span lines.</summary>
        private int SkipValue(int i)
        {
            var depth = 0;
            string? openMultiline = null;
            for (var n = i; n < _lines.Count; n++)
            {
                var s = _lines[n];
                var pos = 0;
                if (n == i)
                {
                    if (KeyPath(s) == null) return i;
                    pos = IndexOfEquals(s) + 1;
                }
                while (pos < s.Length)
                {
                    if (openMultiline != null)
                    {
                        var close = s.IndexOf(openMultiline, pos, StringComparison.Ordinal);
                        if (close < 0) { pos = s.Length; break; }
                        pos = close + 3;
                        openMultiline = null;
                        continue;
                    }
                    var c = s[pos];
                    if (c == '#') break;
                    if (c == '"' || c == '\'')
                    {
                        var triple = new string(c, 3);
                        if (string.CompareOrdinal(s, pos, triple, 0, 3) == 0)
                        {
                            var close = s.IndexOf(triple, pos + 3, StringComparison.Ordinal);
                            if (close < 0) { openMultiline = triple; pos = s.Length; break; }
                            pos = close + 3;
                            continue;
                        }
                        var end = FindStringEnd(s, pos);
                        pos = end < 0 ? s.Length : end + 1;
                        continue;
                    }
                    if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}') depth--;
                    pos++;
                }
                if (depth <= 0 && openMultiline == null) return n;
            }
            return _lines.Count - 1;
        }

        private static int IndexOfEquals(string s)
        {
            var pos = 0;
            var t = s.TrimStart();
            var offset = s.Length - t.Length;
            ReadDottedKey(t, ref pos);
            while (pos < t.Length && t[pos] != '=') pos++;
            return offset + pos;
        }

        private static int FindStringEnd(string s, int start)
        {
            var quote = s[start];
            for (var i = start + 1; i < s.Length; i++)
            {
                if (quote == '"' && s[i] == '\\') { i++; continue; }
                if (s[i] == quote) return i;
            }
            return -1;
        }

        private static string StripComment(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"' || c == '\'')
                {
                    var end = FindStringEnd(value, i);
                    if (end < 0) return value;
                    i = end;
                }
                else if (c == '#') return value.Substring(0, i);
            }
            return value;
        }

        private static bool IsCommentOrBlank(string line)
        {
            var t = line.TrimStart();
            return t.Length == 0 || t[0] == '#';
        }

        private static bool StartsWith(IReadOnlyList<string> path, IReadOnlyList<string> prefix) =>
            path.Count >= prefix.Count && !prefix.Where((p, i) => path[i] != p).Any();

        // ---- values -------------------------------------------------------------------------------------------

        /// <summary>A one-line basic ("…", with escapes) or literal ('…') string.</summary>
        public static bool TryParseString(string text, out string value)
        {
            value = "";
            text = text.Trim();
            if (text.Length < 2) return false;
            if (text[0] == '\'' && text[text.Length - 1] == '\'' && !text.StartsWith("'''", StringComparison.Ordinal))
            {
                value = text.Substring(1, text.Length - 2);
                return value.IndexOf('\'') < 0;
            }
            if (text[0] != '"' || text[text.Length - 1] != '"' || text.StartsWith("\"\"\"", StringComparison.Ordinal)) return false;

            var sb = new StringBuilder();
            for (var i = 1; i < text.Length - 1; i++)
            {
                var c = text[i];
                if (c == '"') return false; // an unescaped quote: not one TOML string (Codex would refuse the file)
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= text.Length - 1) return false;
                switch (text[i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'b': sb.Append('\b'); break;
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u':
                    case 'U':
                        var len = text[i] == 'u' ? 4 : 8;
                        if (i + len >= text.Length) return false;
                        if (!int.TryParse(text.Substring(i + 1, len), System.Globalization.NumberStyles.HexNumber, null, out var cp)) return false;
                        sb.Append(char.ConvertFromUtf32(cp));
                        i += len;
                        break;
                    default: return false;
                }
            }
            value = sb.ToString();
            return true;
        }

        public static bool TryParseStringArray(string text, out IReadOnlyList<string> values)
        {
            values = Array.Empty<string>();
            var t = text.Trim();
            if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') return false;
            var items = new List<string>();
            var i = 1;
            while (i < t.Length - 1)
            {
                var c = t[i];
                if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
                if (c == '#') { while (i < t.Length - 1 && t[i] != '\n') i++; continue; }
                if (c != '"' && c != '\'') return false;
                var end = FindStringEnd(t, i);
                if (end < 0 || !TryParseString(t.Substring(i, end - i + 1), out var item)) return false;
                items.Add(item);
                i = end + 1;
            }
            values = items;
            return true;
        }

        /// <summary>A literal string when the value reads better that way (a Windows path), a basic string otherwise.</summary>
        public static string FormatString(string value)
        {
            var hasControl = value.Any(ch => ch < 0x20 || ch == 0x7f);
            if (value.IndexOf('\\') >= 0 && value.IndexOf('\'') < 0 && !hasControl)
                return "'" + value + "'";

            var sb = new StringBuilder("\"");
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20 || ch == 0x7f) sb.Append("\\u").Append(((int)ch).ToString("X4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string FormatKey(string key) =>
            key.Length > 0 && key.All(ch => char.IsLetterOrDigit(ch) && ch < 128 || ch == '_' || ch == '-') ? key : FormatString(key);
    }
}
