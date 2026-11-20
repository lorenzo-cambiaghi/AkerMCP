#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AkerMcp.Shared.Scripting
{
    /// <summary>The three parts a submitted snippet is split into before it is wrapped.</summary>
    public readonly struct ScriptSourceParts
    {
        public ScriptSourceParts(string usings, string types, string body)
        {
            Usings = usings;
            Types = types;
            Body = body;
        }

        /// <summary>Leading `using` directives, to be emitted at file scope.</summary>
        public string Usings { get; }

        /// <summary>Top-level type declarations, to be emitted at file scope (outside the wrapper class).</summary>
        public string Types { get; }

        /// <summary>Everything else: the statements that become the wrapper method's body.</summary>
        public string Body { get; }
    }

    /// <summary>
    /// Splits a submitted C# snippet into the pieces that belong at file scope and the pieces that belong
    /// inside the generated method.
    ///
    /// <para><b>Why this exists.</b> Every engine executor wraps the snippet as the body of a generated
    /// method. That makes two perfectly ordinary things illegal: `using` directives and type declarations —
    /// C# allows neither inside a method body. Rejecting them would push callers into workarounds
    /// (reflection instead of a helper class, copy-pasted lambdas instead of a type) for no reason other
    /// than how the wrapper happens to be assembled. Lifting them out costs one pass over the text.</para>
    ///
    /// <para><b>Why hand-written instead of Roslyn.</b> This assembly is referenced by the server, which has
    /// no business carrying a compiler; the executors that do have Roslyn are three separate plugins that
    /// cannot share a file. A dependency-free splitter can live in one place and serve all of them — and it
    /// only has to answer one narrow question: what sits at brace depth zero.</para>
    /// </summary>
    public static class ScriptSourceSplitter
    {
        // Keywords that introduce a type. `record` is contextual (it is a legal identifier), which is why a
        // candidate is only accepted when everything before it is attributes and modifiers.
        private static readonly string[] TypeKeywords = { "class", "struct", "interface", "enum", "record", "delegate" };

        // Modifiers that may legally precede a type declaration.
        private static readonly HashSet<string> TypeModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "static", "sealed", "abstract",
            "partial", "readonly", "ref", "unsafe", "new", "file"
        };

        // Illegal on a type at file scope (CS1527): a snippet that writes `private class Foo` is being
        // idiomatic, not wrong — it just doesn't know where its type will land.
        private static readonly HashSet<string> ScopeIllegalModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "private", "protected"
        };

        /// <summary>Splits <paramref name="code"/> into file-scope usings, file-scope types, and body.</summary>
        public static ScriptSourceParts Split(string code)
        {
            if (string.IsNullOrEmpty(code)) return new ScriptSourceParts(string.Empty, string.Empty, code ?? string.Empty);

            var (usings, rest) = HoistUsingDirectives(code);
            var (types, body) = HoistTypeDeclarations(rest);
            return new ScriptSourceParts(usings, types, body);
        }

        // ── Using directives ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Splits leading using-directives off the snippet so they can be emitted at file scope. Only
        /// contiguous directives (interleaved with blank/comment lines) at the very top are hoisted; the
        /// first real statement ends the scan.
        /// </summary>
        public static (string usings, string body) HoistUsingDirectives(string code)
        {
            var usings = new StringBuilder();
            var bodyLines = new List<string>();
            var pending = new List<string>();
            bool leading = true;
            bool inBlockComment = false;

            using var reader = new StringReader(code);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (leading)
                {
                    // The line without its comments: a `/* header */` before the first directive, or a `// why` after
                    // one, used to end the scan — and the directives below it stayed in the body, where they do not compile.
                    var significant = WithoutComments(line, ref inBlockComment);
                    if (significant.Length == 0)
                    {
                        pending.Add(line);
                        continue;
                    }
                    if (IsUsingDirective(significant, out var directive))
                    {
                        usings.AppendLine(directive);
                        continue;
                    }
                    leading = false;
                    bodyLines.AddRange(pending);
                    pending.Clear();
                }
                bodyLines.Add(line);
            }
            bodyLines.AddRange(pending);
            return (usings.ToString(), string.Join("\n", bodyLines));
        }

        // The code on one line of the leading block, comments replaced by a space; `inBlock` carries a /* */ across lines.
        private static string WithoutComments(string line, ref bool inBlock)
        {
            var code = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                if (inBlock)
                {
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) break;
                    inBlock = false;
                    code.Append(' ');
                    i = end + 2;
                    continue;
                }
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlock = true;
                    i += 2;
                    continue;
                }
                code.Append(line[i]);
                i++;
            }
            return code.ToString().Trim();
        }

        // A using-DIRECTIVE (`using System.Linq;`, `using static UnityEngine.Mathf;`, `using GO = ...;`, the tuple alias
        // `using Pair = (int A, int B);`), never a using-STATEMENT or DECLARATION (`using (x)`, `using var x = ...`,
        // `using System.IDisposable d = ...;`): hoisting one of those out of the body moves a disposal scope somewhere it
        // means nothing. An alias names ONE identifier before the '='; a declaration names a type and a variable.
        // `global using` is emitted as a plain using: the wrapper writes its own usings first, and a global one after
        // them does not compile (CS8915).
        private static bool IsUsingDirective(string text, out string directive)
        {
            directive = "";
            if (StartsWithWord(text, 0, "global")) text = text.Substring("global".Length).TrimStart();
            if (!StartsWithWord(text, 0, "using")) return false;
            if (!text.EndsWith(";", StringComparison.Ordinal)) return false;

            var tail = text.Substring("using".Length, text.Length - "using".Length - 1).Trim();
            if (tail.Length == 0 || tail.StartsWith("(", StringComparison.Ordinal)) return false;
            if (StartsWithWord(tail, 0, "var")) return false;

            int equals = tail.IndexOf('=');
            if (equals >= 0)
            {
                var alias = tail.Substring(0, equals).Trim();
                var target = tail.Substring(equals + 1).Trim();
                if (!IsIdentifier(alias) || target.Length == 0) return false;
                if (!target.StartsWith("(", StringComparison.Ordinal) && target.IndexOf('(') >= 0) return false;
                directive = $"using {alias} = {target};";
                return true;
            }

            if (tail.IndexOf('(') >= 0) return false;
            directive = $"using {tail};";
            return true;
        }

        private static bool IsIdentifier(string text)
        {
            if (text.Length == 0 || !IsIdentifierStart(text[0])) return false;
            return ReadWordEnd(text, 0) == text.Length;
        }

        // ── Type declarations ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lifts every top-level type declaration (class / struct / interface / enum / record / delegate)
        /// out of the snippet. Nested types are left alone: they travel with the type that contains them.
        /// </summary>
        public static (string types, string body) HoistTypeDeclarations(string code)
        {
            var types = new StringBuilder();
            var body = new StringBuilder(code.Length);

            int depth = 0;          // brace nesting; declarations only count at zero
            int boundary = 0;       // start of the current top-level element (after the last ; { })
            int copied = 0;         // how much of the source has been flushed to the body
            int i = 0;

            while (i < code.Length)
            {
                int skipped = SkipNonCode(code, i);
                if (skipped > i) { i = skipped; continue; }

                char c = code[i];

                if (c == '{' || c == '}')
                {
                    if (c == '{') depth++;
                    else if (depth > 0) depth--;
                    i++;
                    if (depth == 0) boundary = i;
                    continue;
                }

                if (c == ';')
                {
                    i++;
                    if (depth == 0) boundary = i;
                    continue;
                }

                if (depth != 0 || !IsIdentifierStart(c)) { i++; continue; }

                int wordEnd = ReadWordEnd(code, i);
                string word = code.Substring(i, wordEnd - i);

                if (!IsTypeKeyword(word) || !IsOnlyAttributesAndModifiers(code, boundary, i)
                    || (word == "record" && !IsRecordDeclaration(code, wordEnd)))
                {
                    i = wordEnd;
                    continue;
                }

                int end = FindDeclarationEnd(code, wordEnd);
                if (end < 0) { i = wordEnd; continue; }      // unterminated: leave it to the compiler to complain

                body.Append(code, copied, boundary - copied);
                types.AppendLine(StripScopeIllegalModifiers(code.Substring(boundary, end - boundary)).Trim());

                copied = end;
                boundary = end;
                i = end;
            }

            body.Append(code, copied, code.Length - copied);
            return (types.ToString(), body.ToString());
        }

        // `record` opens a declaration only as `record Name`, `record class Name` or `record struct Name`, followed by its
        // parameter list, body, type parameters, base list or constraints. Anywhere else it is an identifier: a local
        // called record at the start of a statement (`record.Append("x");`) was lifted to file scope as a declaration.
        private static bool IsRecordDeclaration(string code, int afterKeyword)
        {
            int i = SkipCommentsAndSpace(code, afterKeyword);
            if (i >= code.Length || !IsIdentifierStart(code[i])) return false;
            int end = ReadWordEnd(code, i);
            var word = code.Substring(i, end - i);
            if (word == "class" || word == "struct")
            {
                i = SkipCommentsAndSpace(code, end);
                if (i >= code.Length || !IsIdentifierStart(code[i])) return false;
                end = ReadWordEnd(code, i);
            }
            i = SkipCommentsAndSpace(code, end);
            if (i >= code.Length) return false;
            char next = code[i];
            return next == '(' || next == '{' || next == '<' || next == ':' || StartsWithWord(code, i, "where");
        }

        private static int SkipCommentsAndSpace(string code, int i)
        {
            while (i < code.Length)
            {
                if (char.IsWhiteSpace(code[i])) { i++; continue; }
                if (code[i] == '/' && i + 1 < code.Length && (code[i + 1] == '/' || code[i + 1] == '*'))
                {
                    i = SkipNonCode(code, i);
                    continue;
                }
                break;
            }
            return i;
        }

        // Walks to the end of a declaration: the matching '}' of its body, or the ';' of a bodyless one
        // (`delegate void D();`, `record Point(int X, int Y);`).
        private static int FindDeclarationEnd(string code, int from)
        {
            int i = from;
            int depth = 0;

            while (i < code.Length)
            {
                int skipped = SkipNonCode(code, i);
                if (skipped > i) { i = skipped; continue; }

                char c = code[i];
                if (c == '{')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    depth--;
                    i++;
                    if (depth <= 0) return i;
                    continue;
                }
                if (c == ';' && depth == 0) return i + 1;
                i++;
            }
            return -1;
        }

        // True when the span holds nothing but attributes, modifiers, whitespace and comments — i.e. the
        // keyword really opens a declaration, and is not an identifier that happens to read like one
        // (`var record = ...`, `Log(item.record);`).
        private static bool IsOnlyAttributesAndModifiers(string code, int start, int end)
        {
            int i = start;
            while (i < end)
            {
                int skipped = SkipNonCode(code, i);
                if (skipped > i) { i = Math.Min(skipped, end); continue; }

                char c = code[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '[')
                {
                    int close = SkipBracketed(code, i);
                    if (close < 0 || close > end) return false;
                    i = close;
                    continue;
                }

                if (!IsIdentifierStart(c)) return false;

                int wordEnd = ReadWordEnd(code, i);
                if (wordEnd > end || !TypeModifiers.Contains(code.Substring(i, wordEnd - i))) return false;
                i = wordEnd;
            }
            return true;
        }

        // A type that lands at file scope cannot stay `private`/`protected`; the rest of the prefix
        // (attributes, `public`, `sealed`, `partial`, …) is kept exactly as written.
        private static string StripScopeIllegalModifiers(string declaration)
        {
            var result = new StringBuilder(declaration.Length);
            int i = 0;

            while (i < declaration.Length)
            {
                int skipped = SkipNonCode(declaration, i);
                if (skipped > i) { result.Append(declaration, i, skipped - i); i = skipped; continue; }

                char c = declaration[i];
                if (!IsIdentifierStart(c)) { result.Append(c); i++; continue; }

                int wordEnd = ReadWordEnd(declaration, i);
                string word = declaration.Substring(i, wordEnd - i);

                // Only the prefix is rewritten: once the type keyword is reached, the body is untouched —
                // a `private` field inside the type must stay private.
                if (IsTypeKeyword(word))
                {
                    result.Append(declaration, i, declaration.Length - i);
                    return result.ToString();
                }

                if (!ScopeIllegalModifiers.Contains(word)) result.Append(word);
                i = wordEnd;
            }
            return result.ToString();
        }

        // ── Lexical helpers ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// If <paramref name="i"/> opens a comment, string, verbatim/interpolated string or char literal,
        /// returns the index just past it; otherwise returns <paramref name="i"/>. Everything else in this
        /// file relies on it: a `class` inside a string or a comment must not be mistaken for a declaration.
        /// </summary>
        private static int SkipNonCode(string code, int i)
        {
            char c = code[i];

            if (c == '/' && i + 1 < code.Length)
            {
                if (code[i + 1] == '/')
                {
                    int end = code.IndexOf('\n', i);
                    return end < 0 ? code.Length : end + 1;
                }
                if (code[i + 1] == '*')
                {
                    int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    return end < 0 ? code.Length : end + 2;
                }
                return i;
            }

            if (c == '"' || c == '$' || c == '@')
            {
                // The prefix: any number of '$' (a raw interpolated string takes more than one) and at most one '@'.
                int open = i;
                bool interpolated = false, verbatim = false;
                while (open < code.Length && (code[open] == '$' || code[open] == '@'))
                {
                    if (code[open] == '$') interpolated = true; else verbatim = true;
                    open++;
                }
                if (open >= code.Length || code[open] != '"') return i;    // `$` alone, or an @identifier

                // C# 11 raw strings: """ … """. Read as an empty "" and then a string of its own, the second quote pair
                // swallowed the text after the literal, and a class declared INSIDE the literal was moved out of it.
                int quotes = CountRun(code, open, '"');
                if (quotes >= 3 && !verbatim) return SkipRawString(code, open, quotes);
                if (interpolated) return SkipInterpolated(code, open, verbatim);
                return verbatim ? SkipVerbatimString(code, open) : SkipQuoted(code, open, '"');
            }
            if (c == '\'') return SkipQuoted(code, i, '\'');

            return i;
        }

        private static int CountRun(string code, int start, char c)
        {
            int i = start;
            while (i < code.Length && code[i] == c) i++;
            return i - start;
        }

        // Closed by a run of as many quotes as opened it; quotes and backslashes inside are content.
        private static int SkipRawString(string code, int open, int quoteCount)
        {
            int i = open + quoteCount;
            while (i < code.Length)
            {
                if (code[i] != '"') { i++; continue; }
                int run = CountRun(code, i, '"');
                if (run >= quoteCount) return i + run;
                i += run;
            }
            return code.Length;
        }

        // $"…{expr}…": a hole is code and may hold strings of its own ($"{Fmt("x")}"), so the literal ends at the first
        // quote outside every hole — the plain skip stopped at the quote inside the hole.
        private static int SkipInterpolated(string code, int openQuote, bool verbatim)
        {
            int i = openQuote + 1;
            int depth = 0;
            while (i < code.Length)
            {
                char c = code[i];
                if (depth == 0)
                {
                    if (!verbatim && c == '\\') { i += 2; continue; }
                    if (c == '"')
                    {
                        if (verbatim && i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; }
                        return i + 1;
                    }
                    if (c == '{')
                    {
                        if (i + 1 < code.Length && code[i + 1] == '{') { i += 2; continue; }   // {{ is a literal brace
                        depth = 1;
                        i++;
                        continue;
                    }
                    if (c == '\n' && !verbatim) return i;       // unterminated literal: don't swallow the file
                    i++;
                    continue;
                }

                int skipped = SkipNonCode(code, i);
                if (skipped > i) { i = skipped; continue; }
                if (c == '{') depth++;
                else if (c == '}') depth--;
                i++;
            }
            return code.Length;
        }

        private static int SkipQuoted(string code, int openQuote, char quote)
        {
            int i = openQuote + 1;
            while (i < code.Length)
            {
                if (code[i] == '\\') { i += 2; continue; }
                if (code[i] == quote) return i + 1;
                if (code[i] == '\n') return i;              // unterminated literal: don't swallow the file
                i++;
            }
            return code.Length;
        }

        private static int SkipVerbatimString(string code, int openQuote)
        {
            int i = openQuote + 1;
            while (i < code.Length)
            {
                if (code[i] != '"') { i++; continue; }
                if (i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; }   // "" escape
                return i + 1;
            }
            return code.Length;
        }

        // Skips a balanced [...] group (attribute lists), honouring nested brackets and literals.
        private static int SkipBracketed(string code, int open)
        {
            int i = open + 1;
            int depth = 1;
            while (i < code.Length)
            {
                int skipped = SkipNonCode(code, i);
                if (skipped > i) { i = skipped; continue; }

                if (code[i] == '[') depth++;
                else if (code[i] == ']' && --depth == 0) return i + 1;
                i++;
            }
            return -1;
        }

        private static bool IsTypeKeyword(string word)
        {
            for (int i = 0; i < TypeKeywords.Length; i++)
                if (string.Equals(word, TypeKeywords[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static int ReadWordEnd(string code, int start)
        {
            int i = start;
            while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
            return i;
        }

        private static bool StartsWithWord(string text, int index, string word)
        {
            if (index + word.Length > text.Length) return false;
            if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0) return false;
            int after = index + word.Length;
            return after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
        }
    }
}
