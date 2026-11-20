#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;
using UnityEditor;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Scripting;
using Debug = UnityEngine.Debug;

namespace AkerMcp.Unity
{
    public class ScriptGlobals
    {
        public GameObject? selectedObject => Selection.activeGameObject;

        public GameObject? Find(string name) => GameObject.Find(name);

        public T[] FindAll<T>() where T : UnityEngine.Object
            => UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);

        public GameObject Create(string name) => new GameObject(name);

        public void Log(object? message) => Debug.Log(message);
    }

    public class DynamicEvaluatorV2 : ICodeExecutor
    {
        private IReadOnlyList<MetadataReference>? _references;
        private int _referencedAssemblyCount = -1;
        // Bumped on every rebuild of _references: part of the compiled-snippet key, so a snippet compiled against an
        // older reference set is compiled again rather than reused.
        private int _referencesVersion;
        private readonly CompiledSnippets _compiled = new CompiledSnippets(capacity: 32);
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly StringBuilder _outputCapture = new StringBuilder();

        public DynamicEvaluatorV2(IMainThreadDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// The reference set, rebuilt whenever the AppDomain has gained assemblies that can be referenced.
        /// <para>
        /// ⚠️ It must NOT be captured once in the constructor, and this is the whole point: .NET
        /// loads assemblies LAZILY, and this plugin is recreated on the first editor tick after
        /// every domain reload — precisely the moment when the fewest of them are loaded. A
        /// snapshot taken there stays partial for the rest of the session, and the symptom is
        /// brutal to diagnose from the outside: <c>execute</c> keeps compiling, but reports
        /// "The name 'UnityEngine' does not exist in the current context" for every snippet, and
        /// never recovers. Observed after a Play Mode cycle (two reloads): <c>Log(object)</c>
        /// still worked while <c>Create(string)</c> did not — same class, but only the latter has
        /// a UnityEngine type in its signature. That asymmetry is the fingerprint of a reference
        /// list that kept the plugin's own assembly and lost the engine's.
        /// </para>
        /// <para>
        /// The key is the number of FILE-BACKED assemblies — the only ones that can be referenced — and not the
        /// AppDomain's total. Every successful snippet is <c>Assembly.Load</c>ed from bytes and grows the total by
        /// one, so a total-count key missed on every call after a success: each one rebuilt the whole reference set,
        /// reading the project DLLs into memory, on the main thread — and assert_state polls execute every 250 ms.
        /// Assemblies loaded from bytes have no Location, so they never move this key. It is still sound: an
        /// AppDomain never unloads an assembly, so the count only grows until the next reload, which replaces this
        /// instance anyway.
        /// </para>
        /// </summary>
        private IReadOnlyList<MetadataReference> References
        {
            get
            {
                var located = LocatedAssemblies();
                if (_references == null || located.Count != _referencedAssemblyCount)
                {
                    _references = BuildReferences(located);
                    _referencedAssemblyCount = located.Count;
                    _referencesVersion++;
                }
                return _references;
            }
        }

        public async Task<CodeExecutionResult> Execute(string code, int timeoutMs = 5000, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                var result = await _dispatcher.RunOnMainThread(() => ExecuteInternal(code), cts.Token);

                sw.Stop();
                result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                return result;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"Execution timed out after {timeoutMs}ms. The script could NOT be aborted: " +
                            "it runs on the engine main thread and may still be running or may have completed. " +
                            "Verify scene state (inspect / get_console_logs) before retrying.",
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}\nStack Trace:\n{ex.StackTrace}",
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
        }

        private const int MaxCapturedOutputChars = 16_000;

        // The Roslyn that ships with Unity's Mono is 3.7, whose DEFAULT language version is C# 8 — while
        // the editor compiles the project itself at C# 9. Left at the default, this executor would reject
        // syntax that is perfectly legal everywhere else in the same project (records, `is not`,
        // target-typed `new`), which reads as a bug in the tool rather than a version mismatch. `Preview`
        // is what turns those on in 3.7, and on any newer Roslyn it simply keeps the executor as permissive
        // as the compiler allows — the right default for throwaway snippets.
        private static readonly CSharpParseOptions ScriptParseOptions =
            new CSharpParseOptions(LanguageVersion.Preview);

        private CodeExecutionResult ExecuteInternal(string code)
        {
            // Capture Debug.Log / Log() emitted while the script runs so the model
            // receives them in the 'output' field. Execution happens on the main
            // thread, so logMessageReceived fires synchronously within this scope.
            _outputCapture.Clear();
            Application.logMessageReceived += CaptureLog;
            CodeExecutionResult result;
            try
            {
                result = CompileAndRun(code);
            }
            finally
            {
                Application.logMessageReceived -= CaptureLog;
            }

            if (_outputCapture.Length > 0)
                result.Output = _outputCapture.ToString().TrimEnd();
            return result;
        }

        private void CaptureLog(string condition, string stackTrace, LogType type)
        {
            if (_outputCapture.Length >= MaxCapturedOutputChars)
                return;
            _outputCapture.AppendLine(type == LogType.Log ? condition : $"[{type}] {condition}");
            if (_outputCapture.Length >= MaxCapturedOutputChars)
                _outputCapture.AppendLine("…output truncated.");
        }

        private CodeExecutionResult CompileAndRun(string code)
        {
            try
            {
                var references = References;
                var key = _referencesVersion.ToString(CultureInfo.InvariantCulture) + "\n" + code;

                var execute = _compiled.Get(key);
                if (execute == null)
                {
                    var snippet = MappedSnippet.From(code);

                    // A bare expression (`Find("Player").transform.position`) is returned, so a probe needs no
                    // `return ...;` ceremony. Tried only when the snippet parses as one expression and nothing else;
                    // a void call (`Log("x")`) cannot be returned, and compiles again as a statement — with the `;`
                    // it was written without. Its errors are the expression's: that is what the caller wrote.
                    string? errors = null;
                    if (snippet.IsBareExpression(ScriptParseOptions))
                    {
                        execute = Compile(snippet, BodyKind.ReturnedExpression, references, out errors)
                                  ?? Compile(snippet, BodyKind.ExpressionStatement, references, out _);
                    }
                    else
                    {
                        execute = Compile(snippet, BodyKind.Statements, references, out errors);
                    }
                    if (execute == null)
                        return new CodeExecutionResult { Success = false, Error = "Compilation error:\n" + errors };

                    // Only snippets without type declarations are reused: a hoisted type's static fields would
                    // otherwise carry state from one run into the next, which a fresh compile never did. That
                    // still covers the repeated probes (assert_state, sample_state), which no longer emit and
                    // load a new assembly — one that can never be unloaded — on every poll.
                    if (!snippet.HasTypes) _compiled.Add(key, execute);
                }

                var instance = Activator.CreateInstance(execute.DeclaringType!);
                var returnValue = execute.Invoke(instance, null);

                return new CodeExecutionResult
                {
                    Success = true,
                    ReturnValue = returnValue != null ? FormatReturnValue(returnValue) : null
                };
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}"
                };
            }
        }

        private enum BodyKind
        {
            /// <summary>Statements, as written; Execute returns null unless they return something.</summary>
            Statements,
            /// <summary>One expression, whose value Execute returns.</summary>
            ReturnedExpression,
            /// <summary>One expression with no value (a void call), run as a statement.</summary>
            ExpressionStatement,
        }

        // Compiles the snippet and returns its Execute method, or null with the compiler's errors.
        private static MethodInfo? Compile(MappedSnippet snippet, BodyKind kind,
            IReadOnlyList<MetadataReference> references, out string? errors)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(BuildSource(snippet, kind), ScriptParseOptions);
            var compilation = CSharpCompilation.Create(
                "AkerDynamic_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var ms = new MemoryStream())
            {
                var emitResult = compilation.Emit(ms);
                if (!emitResult.Success)
                {
                    errors = string.Join("\n", emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString()));
                    return null;
                }

                errors = null;
                var type = Assembly.Load(ms.ToArray()).GetType("DynamicScript");
                return type?.GetMethod("Execute")
                       ?? throw new InvalidOperationException("Failed to find DynamicScript.Execute in the compiled snippet");
            }
        }

        // The snippet becomes a method body, where `using` directives and type declarations are both illegal. Both
        // are lifted to file scope instead of being refused: see ScriptSourceSplitter for why that is the executor's
        // job and not the caller's. The #line directives make the compiler count lines as the caller wrote them:
        // without them a typo on line 2 came back as "(14,5): error", the line of the generated wrapper.
        private static string BuildSource(MappedSnippet snippet, BodyKind kind)
        {
            var sb = new StringBuilder(snippet.Body.Length + snippet.Types.Length + 1024);
            sb.Append(snippet.Usings);
            sb.Append("#line default\n");
            sb.Append("using System;\n");
            sb.Append("using System.Collections.Generic;\n");
            sb.Append("using System.Linq;\n");
            sb.Append("using System.Text;\n");
            sb.Append("using UnityEngine;\n");
            sb.Append("using UnityEditor;\n");
            sb.Append("using AkerMcp.Unity;\n");
            sb.Append("using UnityEngine.Rendering;\n\n");
            sb.Append(snippet.Types);
            sb.Append("#line default\n");
            // Inheriting ScriptGlobals puts selectedObject / Find / FindAll / Create / Log directly in scope inside
            // Execute (no `globals.` prefix needed).
            sb.Append("public class DynamicScript : ScriptGlobals\n{\n    public object Execute()\n    {\n");
            if (kind == BodyKind.ReturnedExpression) sb.Append("        return\n");
            sb.Append("#line ").Append(snippet.BodyLine.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(snippet.Body).Append('\n');
            sb.Append("#line default\n");
            if (kind == BodyKind.ExpressionStatement) sb.Append("        ;\n");
            sb.Append(kind == BodyKind.ReturnedExpression ? "        ;\n" : "        return null;\n");
            sb.Append("    }\n}\n\n");
            sb.Append(ScriptCompatibilityShims.ForCurrentRuntime());
            return sb.ToString();
        }

        // Include ALL non-dynamic assemblies loaded from a file. This automatically picks up URP, HDRP, Input
        // System, TextMeshPro, Cinemachine, user scripts, and any other UPM package — without maintaining a
        // hardcoded list. Assemblies loaded from bytes (every snippet this executor ran) have no Location.
        private static List<Assembly> LocatedAssemblies()
        {
            var located = new List<Assembly>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                try { if (!string.IsNullOrEmpty(a.Location)) located.Add(a); }
                catch { /* an assembly that refuses to report its location cannot be referenced */ }
            }
            return located;
        }

        private static IReadOnlyList<MetadataReference> BuildReferences(List<Assembly> assemblies)
        {
            // Build metadata references WITHOUT holding file locks on the project's own
            // assemblies. Roslyn's Assembly-based references (and CreateFromFile) memory-map the
            // .dll and keep a read handle open for the plugin's lifetime. For DLLs that Unity
            // overwrites on every recompile (everything under Library/ScriptAssemblies — i.e. the
            // asmdef + Assembly-CSharp DLLs) that lock makes the next compile fail with
            // "Copying the file failed: ... being used by another process". So we load those from
            // an in-memory copy (CreateFromImage), and only file-reference the immutable
            // engine/package DLLs (which Unity never rewrites, so locking them is harmless).
            string scriptAsmDir = Path.GetFullPath("Library/ScriptAssemblies")
                .Replace('\\', '/').TrimEnd('/');
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var references = new List<MetadataReference>(assemblies.Count);
            foreach (var a in assemblies)
            {
                try
                {
                    string loc = Path.GetFullPath(a.Location).Replace('\\', '/');
                    if (!seen.Add(loc)) continue;
                    bool isProjectAsm = loc.StartsWith(scriptAsmDir, StringComparison.OrdinalIgnoreCase);
                    references.Add(isProjectAsm
                        ? MetadataReference.CreateFromImage(File.ReadAllBytes(loc))
                        : MetadataReference.CreateFromFile(loc));
                }
                catch { /* skip unreadable assembly */ }
            }
            return references;
        }

        private static string FormatReturnValue(object value)
        {
            if (value is string s) return s;
            if (value is UnityEngine.Object uObj && uObj == null) return "null (destroyed)";
            if (value is GameObject go) return $"GameObject(\"{go.name}\")";
            if (value is Component comp) return $"{comp.GetType().Name} on \"{comp.gameObject.name}\"";

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    items.Add(item != null ? Invariant(item) : "null");
                    if (items.Count >= 20) { items.Add("..."); break; }
                }
                return $"[{string.Join(", ", items)}]";
            }

            return Invariant(value);
        }

        // The server reads these values back (sample_state, assert_state). Formatted in the editor's culture, an
        // it-IT machine returned 0.5f as "0,5" and a Vector3 as "(0,50, 1,00, 0,00)": the decimal comma read as a
        // separator, and a number became two.
        private static string Invariant(object value)
            => value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString() ?? "null";

        /// <summary>
        /// A small most-recently-used cache of compiled snippets. Touched on the main thread only (every execute runs
        /// there), so it takes no lock.
        /// </summary>
        private sealed class CompiledSnippets
        {
            private readonly int _capacity;
            private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, MethodInfo>>> _byKey =
                new Dictionary<string, LinkedListNode<KeyValuePair<string, MethodInfo>>>(StringComparer.Ordinal);
            private readonly LinkedList<KeyValuePair<string, MethodInfo>> _recency =
                new LinkedList<KeyValuePair<string, MethodInfo>>();

            public CompiledSnippets(int capacity) => _capacity = capacity;

            public MethodInfo? Get(string key)
            {
                if (!_byKey.TryGetValue(key, out var node)) return null;
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Value;
            }

            public void Add(string key, MethodInfo method)
            {
                if (_byKey.ContainsKey(key)) return;
                _byKey[key] = _recency.AddFirst(new KeyValuePair<string, MethodInfo>(key, method));
                while (_recency.Count > _capacity)
                {
                    var last = _recency.Last!;
                    _recency.RemoveLast();
                    _byKey.Remove(last.Value.Key);
                }
            }
        }

        /// <summary>
        /// The snippet split as <see cref="ScriptSourceSplitter"/> splits it — that split is what gets compiled — laid
        /// out so that every piece carries the line it had in the caller's text.
        /// <para>
        /// The splitter hands back the pieces without positions, and lifting a type out of the body shortens it: every
        /// line after the type would be reported too early. So the body gets one newline back for each line a lifted
        /// type took, at the point it was lifted from (code at depth zero, never inside a literal), and each type is
        /// emitted after its own <c>#line</c>. Where the lifts are found by re-running the splitter on prefixes of the
        /// snippet: a prefix ending inside a declaration leaves it unterminated, and its body then stops being a
        /// prefix of the real one. Whatever the reconstruction yields is checked against the splitter's own output; on
        /// any mismatch the pieces are compiled exactly as split, with approximate line numbers.
        /// </para>
        /// </summary>
        private sealed class MappedSnippet
        {
            public string Usings = "";
            public string Types = "";
            public string Body = "";
            /// <summary>The caller's line that the first line of <see cref="Body"/> is.</summary>
            public int BodyLine = 1;
            public bool HasTypes;

            public static MappedSnippet From(string code)
            {
                var parts = ScriptSourceSplitter.Split(code);
                var result = new MappedSnippet { HasTypes = parts.Types.Length > 0 };

                // The hoisted usings are whole lines from the top of the snippet; the body is every other line, in
                // order. Past the first statement the body is therefore exactly `usings` lines behind the caller.
                var usingLines = Lines(parts.Usings).Where(l => l.Length > 0).ToList();
                result.Usings = MapUsings(code, usingLines, parts.Usings);
                int lineOffset = usingLines.Count;
                result.BodyLine = lineOffset + 1;

                if (!result.HasTypes)
                {
                    result.Body = parts.Body;
                    return result;
                }

                var rest = ScriptSourceSplitter.HoistUsingDirectives(code).body;
                if (TryMapTypes(rest, parts, lineOffset, out var body, out var types))
                {
                    result.Body = body;
                    result.Types = types;
                }
                else
                {
                    result.Body = parts.Body;
                    result.Types = parts.Types;
                }
                return result;
            }

            public bool IsBareExpression(CSharpParseOptions options)
            {
                if (string.IsNullOrWhiteSpace(Body)) return false;
                // consumeFullText: anything after the expression (a `;`, a second statement) is a diagnostic.
                return !SyntaxFactory.ParseExpression(Body, 0, options, consumeFullText: true).ContainsDiagnostics;
            }

            private static string MapUsings(string code, List<string> usingLines, string unmapped)
            {
                if (usingLines.Count == 0) return "";
                var sb = new StringBuilder();
                int next = 0;
                var original = Lines(code);
                for (int i = 0; i < original.Count && next < usingLines.Count; i++)
                {
                    if (!string.Equals(original[i].TrimStart(), usingLines[next], StringComparison.Ordinal)) continue;
                    sb.Append("#line ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n')
                      .Append(usingLines[next]).Append('\n');
                    next++;
                }
                return next == usingLines.Count ? sb.ToString() : unmapped;
            }

            private static bool TryMapTypes(string rest, ScriptSourceParts parts, int lineOffset,
                out string body, out string types)
            {
                body = types = "";

                // Every lift starts and ends where the splitter sets a boundary: the start of the text, or just past
                // a `;` or `}`.
                var candidates = new List<int> { 0 };
                for (int i = 0; i < rest.Length; i++)
                    if (rest[i] == ';' || rest[i] == '}') candidates.Add(i + 1);
                if (candidates[candidates.Count - 1] != rest.Length) candidates.Add(rest.Length);

                var mapped = new StringBuilder(rest.Length);
                var plain = new StringBuilder(rest.Length);
                var mappedTypes = new StringBuilder();
                var plainTypes = new StringBuilder();
                int prevQ = 0, prevP = 0;

                for (int c = 1; c < candidates.Count; c++)
                {
                    int q = candidates[c];
                    string prefixBody = q == rest.Length
                        ? parts.Body
                        : ScriptSourceSplitter.HoistTypeDeclarations(rest.Substring(0, q)).body;
                    if (!parts.Body.StartsWith(prefixBody, StringComparison.Ordinal)) continue; // inside a declaration
                    int p = prefixBody.Length;

                    if (p - prevP == q - prevQ)
                    {
                        mapped.Append(rest, prevQ, q - prevQ);
                        plain.Append(rest, prevQ, q - prevQ);
                    }
                    else if (p == prevP)
                    {
                        var segment = rest.Substring(prevQ, q - prevQ);
                        var lifted = ScriptSourceSplitter.HoistTypeDeclarations(segment).types;
                        int firstCode = 0;
                        while (firstCode < segment.Length && char.IsWhiteSpace(segment[firstCode])) firstCode++;
                        int line = lineOffset + 1 + CountNewlines(rest, 0, prevQ + firstCode);
                        mappedTypes.Append("#line ").Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n')
                                   .Append(lifted);
                        plainTypes.Append(lifted);
                        mapped.Append('\n', CountNewlines(segment, 0, segment.Length));
                    }
                    else
                    {
                        return false;
                    }
                    prevQ = q;
                    prevP = p;
                }

                if (prevQ != rest.Length
                    || !string.Equals(plain.ToString(), parts.Body, StringComparison.Ordinal)
                    || !string.Equals(plainTypes.ToString(), parts.Types, StringComparison.Ordinal))
                    return false;

                body = mapped.ToString();
                types = mappedTypes.ToString();
                return true;
            }

            private static int CountNewlines(string text, int start, int end)
            {
                int n = 0;
                for (int i = start; i < end; i++) if (text[i] == '\n') n++;
                return n;
            }

            private static List<string> Lines(string text)
            {
                var lines = new List<string>();
                using var reader = new StringReader(text);
                string? line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines;
            }
        }
    }
}
