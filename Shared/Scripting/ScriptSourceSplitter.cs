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

            using var reader = new StringReader(code);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (leading)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        pending.Add(line);
                        continue;
                    }
                    if (IsUsingDirective(trimmed))
                    {
                        usings.AppendLine(trimmed);
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

        // A using-DIRECTIVE (`using System.Linq;`, `using static UnityEngine.Mathf;`, `using GO = ...;`),
        // never a using-STATEMENT (`using (x)`, `using var x = ...`): the '(' and the `var` give those away,
        // and hoisting one out of the body would move a disposal scope somewhere it means nothing.
        private static bool IsUsingDirective(string trimmedLine)
        {
            if (!StartsWithWord(trimmedLine, 0, "using")) return false;
            if (!trimmedLine.EndsWith(";", StringComparison.Ordinal)) return false;

            var tail = trimmedLine.Substring("using".Length).TrimStart();
            if (tail.Length == 0 || tail.StartsWith("(", StringComparison.Ordinal)) return false;
            if (StartsWithWord(tail, 0, "var")) return false;
            if (tail.IndexOf('(') >= 0) return false;

            // `using X = Y;` (alias) is fine; `using var x = new T();` was already excluded above.
            return true;
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

                if (!IsTypeKeyword(word) || !IsOnlyAttributesAndModifiers(code, boundary, i))
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

            if (c == '@' && i + 1 < code.Length && code[i + 1] == '"') return SkipVerbatimString(code, i + 1);
            if (c == '$' && i + 1 < code.Length && code[i + 1] == '"') return SkipQuoted(code, i + 1, '"');
            if (c == '$' && i + 2 < code.Length && code[i + 1] == '@' && code[i + 2] == '"') return SkipVerbatimString(code, i + 2);
            if (c == '@' && i + 2 < code.Length && code[i + 1] == '$' && code[i + 2] == '"') return SkipVerbatimString(code, i + 2);
            if (c == '"') return SkipQuoted(code, i, '"');
            if (c == '\'') return SkipQuoted(code, i, '\'');

            return i;
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
