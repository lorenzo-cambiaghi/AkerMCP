#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Scripting;
using LogLevel = AkerMcp.Shared.Abstraction.LogLevel;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Globals available inside `execute` scripts. Mirrors the Unity adapter's
    /// surface (selectedObject / Find / FindAll / Create / Log) but in Godot terms.
    /// </summary>
    public class ScriptGlobals
    {
        private readonly StringBuilder _output;

        public ScriptGlobals(StringBuilder output) => _output = output;

        public Node? SceneRoot => EditorInterface.Singleton.GetEditedSceneRoot();

        public Node? selectedObject
        {
            get
            {
                var nodes = EditorInterface.Singleton.GetSelection().GetSelectedNodes();
                return nodes.Count > 0 ? nodes[0] : null;
            }
        }

        public Node? Find(string name) => SceneRoot?.FindChild(name, recursive: true, owned: false);

        public T[] FindAll<T>() where T : Node
        {
            var list = new List<T>();
            var root = SceneRoot;
            if (root != null) Collect(root, list);
            return list.ToArray();
        }

        public Node Create(string name)
        {
            var root = SceneRoot;
            var node = new Node3D { Name = name };
            if (root != null)
            {
                root.AddChild(node);
                node.Owner = root;
            }
            return node;
        }

        // Captured into the result's `output` field (Godot has no global print hook,
        // so GD.Print alone would not be captured — use Log() to surface text).
        public void Log(object? message)
        {
            var text = message?.ToString() ?? "null";
            GD.Print(text);
            // And into the buffer get_console_logs reads, which GD.Print never reaches.
            GodotMcpPlugin.AppendToConsole(LogLevel.Info, text);
            AppendOutput(text);
        }

        // The cap was declared and never applied: a Log() in a loop grew the reply without bound.
        private void AppendOutput(string text)
        {
            if (_output.Length >= GodotCodeExecutor.MaxCapturedOutputChars)
                return;
            _output.AppendLine(text);
            if (_output.Length >= GodotCodeExecutor.MaxCapturedOutputChars)
                _output.AppendLine("…output truncated.");
        }

        private static void Collect<T>(Node node, List<T> list) where T : Node
        {
            if (node is T t) list.Add(t);
            foreach (Node child in node.GetChildren()) Collect(child, list);
        }
    }

    public class GodotCodeExecutor : ICodeExecutor
    {
        internal const int MaxCapturedOutputChars = 16_000;

        // Snippet assemblies are named with this prefix, which also keeps them out of the references.
        private const string DynamicAssemblyPrefix = "AkerDynamic_";
        // The file name diagnostics report for the caller's own lines (`#line N "snippet"`).
        private const string SnippetFile = "snippet";

        private readonly IMainThreadDispatcher _dispatcher;
        private readonly StringBuilder _outputCapture = new();

        // Where Godot builds and loads the project assembly from (GodotSharpDirs:
        // <project data dir>/mono/temp/bin/Debug), for the assemblies it loads from memory.
        private readonly string _projectDir;
        private readonly string _projectBinDir;

        // The context the adapter itself was loaded in: Godot's PluginLoadContext in the editor.
        private readonly AssemblyLoadContext _hostContext;

        // Reference cache (main thread only, like every execute). Rebuilding the references read
        // every loaded assembly's bytes again on every call, a few hundred files each time.
        private Dictionary<string, (DateTime stamp, MetadataReference reference)> _referenceByFile =
            new(StringComparer.OrdinalIgnoreCase);
        private string? _referenceSetKey;
        private List<MetadataReference>? _referenceSet;

        public GodotCodeExecutor(IMainThreadDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _projectDir = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
            bool hiddenDataDir = ProjectSettings.GetSetting("application/config/use_hidden_project_data_directory", true).AsBool();
            _projectBinDir = Path.Combine(_projectDir, hiddenDataDir ? ".godot" : "godot", "mono", "temp", "bin", "Debug");
            _hostContext = AssemblyLoadContext.GetLoadContext(typeof(ScriptGlobals).Assembly) ?? AssemblyLoadContext.Default;
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

        private CodeExecutionResult ExecuteInternal(string code)
        {
            _outputCapture.Clear();
            var result = CompileAndRun(code);
            if (_outputCapture.Length > 0)
                result.Output = _outputCapture.ToString().TrimEnd();
            return result;
        }

        // Snippets are throwaway code: the executor should accept whatever the host compiler can parse,
        // not the conservative default (which lags the language version the project itself is built with).
        private static readonly CSharpParseOptions ScriptParseOptions =
            new CSharpParseOptions(LanguageVersion.Preview);

        private CodeExecutionResult CompileAndRun(string code)
        {
            try
            {
                var snippet = SnippetSource.From(code ?? string.Empty);
                var references = GetReferences();

                // A bare expression ("SceneRoot.GetChildCount()") answers with its value, as in a
                // REPL; it used to be a missing-semicolon error. A void call written the same way
                // ("Log(1)") cannot be returned, so it is retried as a statement.
                var emitted = Emit(snippet.Build(snippet.IsExpression ? BodyForm.Return : BodyForm.AsWritten), references, out var errors);
                if (emitted == null && snippet.IsExpression)
                {
                    // The errors of the return form are the ones reported if this fails too: the
                    // caller wrote an expression, and "; expected" would not be about their code.
                    emitted = Emit(snippet.Build(BodyForm.Statement), references, out _);
                }
                if (emitted == null)
                    return new CodeExecutionResult { Success = false, Error = "Compilation error:\n" + errors };

                return Run(emitted);
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

        private static MemoryStream? Emit(string source, List<MetadataReference> references, out string errors)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, ScriptParseOptions);
            var compilation = CSharpCompilation.Create(
                DynamicAssemblyPrefix + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (emit.Success)
            {
                errors = "";
                ms.Position = 0;
                return ms;
            }

            ms.Dispose();
            // With the #line directives these read "snippet(3,5): error CS0103: …", the caller's own
            // line and column; before, they were lines of the generated wrapper file.
            errors = string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return null;
        }

        private CodeExecutionResult Run(MemoryStream image)
        {
            // A collectible context per call. Assembly.Load(byte[]) put every snippet in a context
            // of its own that could never be unloaded: each execute leaked one assembly for the
            // life of the editor. Collectible is also the only kind allowed to reference the
            // project assembly, which Godot loads collectible itself (to hot-reload it).
            var context = new SnippetLoadContext(_hostContext);
            try
            {
                using (image)
                {
                    var assembly = context.LoadFromStream(image);
                    var type = assembly.GetType("DynamicScript")!;
                    var instance = Activator.CreateInstance(type, _outputCapture);
                    var method = type.GetMethod("Execute")!;

                    var returnValue = method.Invoke(instance, null);

                    return new CodeExecutionResult
                    {
                        Success = true,
                        ReturnValue = returnValue != null ? FormatReturnValue(returnValue) : null
                    };
                }
            }
            finally
            {
                // Unload only starts the collection. Whatever the snippet left reachable (a signal
                // connected to one of its lambdas, an instance of a type it declared) keeps its
                // context alive until released — the code it points at has to stay. Everything
                // else is collected.
                context.Unload();
            }
        }

        /// <summary>
        /// Loads a snippet. Its dependencies come from the context the adapter lives in: in the
        /// editor that is Godot's PluginLoadContext, the only one holding the project assembly, and
        /// with it the ScriptGlobals the snippet class derives from. Left to the default context,
        /// the snippet could not load the class it was compiled against.
        /// </summary>
        private sealed class SnippetLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyLoadContext _host;

            public SnippetLoadContext(AssemblyLoadContext host) : base("AkerMcp execute", isCollectible: true)
                => _host = host;

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                try { return _host.LoadFromAssemblyName(assemblyName); }
                catch { return null; } // the runtime then tries the default context
            }
        }

        // --- references ------------------------------------------------------------------

        private List<MetadataReference> GetReferences()
        {
            var files = ReferenceFiles();
            var key = string.Join("|", files.Select(f => f.path + "@" + f.stamp.Ticks.ToString(CultureInfo.InvariantCulture)));
            if (_referenceSet != null && key == _referenceSetKey)
                return _referenceSet;

            // Only the files that are new or changed are read again.
            var next = new Dictionary<string, (DateTime stamp, MetadataReference reference)>(StringComparer.OrdinalIgnoreCase);
            var references = new List<MetadataReference>(files.Count);
            foreach (var (path, stamp) in files)
            {
                if (!_referenceByFile.TryGetValue(path, out var entry) || entry.stamp != stamp)
                {
                    try { entry = (stamp, CreateReference(path)); }
                    catch { continue; } // unreadable: compile without it
                }
                next[path] = entry;
                references.Add(entry.reference);
            }

            _referenceByFile = next;
            _referenceSetKey = key;
            _referenceSet = references;
            return references;
        }

        // Files inside the project are read into memory, so the reference never locks them: Godot's
        // next build overwrites the project's .godot/mono output. The runtime's and the editor's own
        // assemblies never change while the editor runs, so they are mapped from disk instead of
        // copied (they are most of the bytes).
        private MetadataReference CreateReference(string path)
        {
            if (path.StartsWith(_projectDir, StringComparison.OrdinalIgnoreCase))
                return MetadataReference.CreateFromImage(File.ReadAllBytes(path), filePath: path);
            return MetadataReference.CreateFromFile(path);
        }

        // Every loaded, non-dynamic assembly as a file: GodotSharp, the project assembly, AkerMcp.*,
        // Roslyn and the other NuGet dependencies. One per simple name (after a C# reload the editor
        // can briefly hold two copies of the project's assemblies, and two references with the same
        // identity are CS1703), the highest version winning.
        private List<(string path, DateTime stamp)> ReferenceFiles()
        {
            var byName = new Dictionary<string, (string path, Version version)>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var name = assembly.GetName();
                if (string.IsNullOrEmpty(name.Name) || name.Name.StartsWith(DynamicAssemblyPrefix, StringComparison.Ordinal))
                    continue;

                var path = FileOf(assembly, name.Name);
                if (path == null) continue;

                var version = name.Version ?? new Version(0, 0);
                if (byName.TryGetValue(name.Name, out var seen) && seen.version >= version) continue;
                byName[name.Name] = (path, version);
            }

            var files = new List<(string path, DateTime stamp)>(byName.Count);
            foreach (var (_, (path, _)) in byName.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                try { files.Add((path, File.GetLastWriteTimeUtc(path))); }
                catch { /* vanished: skip */ }
            }
            return files;
        }

        private string? FileOf(Assembly assembly, string simpleName)
        {
            string location;
            try { location = assembly.Location; } catch { location = ""; }
            if (!string.IsNullOrEmpty(location)) return location;

            // Godot loads the project assembly and every dependency it ships with from memory
            // (PluginLoadContext.Load → LoadFromStream, so the files stay free for the next build),
            // which leaves Location empty. Those used to be skipped, and with them the project
            // assembly — ScriptGlobals included, the base class of every snippet. Their files are
            // where Godot loaded them from: the project's build output.
            var candidate = Path.Combine(_projectBinDir, simpleName + ".dll");
            return File.Exists(candidate) ? candidate : null;
        }

        // --- formatting ------------------------------------------------------------------

        private static string FormatReturnValue(object value)
        {
            if (value is string s) return s;
            if (value is Node node) return $"{node.GetType().Name}(\"{node.Name}\")";

            if (value is System.Collections.IEnumerable enumerable && value is not string)
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

        // The return value is data the server reads back (sample_state, assert_state), not prose:
        // under the editor's culture a float 0.5 came back as "0,5" on an it-IT machine.
        private static string Invariant(object value)
            => value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString() ?? "null";

        // --- source ----------------------------------------------------------------------

        private enum BodyForm
        {
            /// <summary>The body as the caller wrote it.</summary>
            AsWritten,
            /// <summary>A bare expression returned as the result: <c>return expr;</c>.</summary>
            Return,
            /// <summary>A bare expression run for its effect: <c>expr;</c> (a void call).</summary>
            Statement
        }

        /// <summary>
        /// The generated source for one snippet. The snippet becomes a method body, where `using`
        /// directives and type declarations are both illegal; both are lifted to file scope instead of
        /// being refused (see ScriptSourceSplitter for why that is the executor's job). Lifting them
        /// moves the remaining lines, so `#line` directives map every line back to where the caller
        /// wrote it: errors used to point at lines of the generated wrapper.
        /// </summary>
        private sealed class SnippetSource
        {
            private string _usings = "";
            private string _types = "";
            private string _body = "";
            private int _bodyFirstLine = 1;

            public bool IsExpression { get; private set; }

            public static SnippetSource From(string code)
            {
                var parts = ScriptSourceSplitter.Split(code);
                var source = new SnippetSource { _usings = parts.Usings, IsExpression = IsBareExpression(parts.Body) };

                // The using lines are the only lines removed ahead of the body, and only from the top:
                // from the first statement on, every line sits exactly that many lines lower.
                int usingLines = CountNewlines(parts.Usings);
                source._bodyFirstLine = usingLines + 1;

                if (parts.Types.Length == 0)
                {
                    source._body = parts.Body;
                    return source;
                }

                // Types were lifted out of the body too. Where the lifted ranges are known exactly,
                // each is replaced by as many line breaks as it held (whitespace between tokens: the
                // code is unchanged) and each type gets the line it was written on. Unknown, the body
                // stays the splitter's and only the lines up to the first type map exactly.
                var rest = ScriptSourceSplitter.HoistUsingDirectives(code).body;
                if (TryLineExactParts(rest, parts, usingLines, out var types, out var body))
                {
                    source._types = types;
                    source._body = body;
                }
                else
                {
                    source._types = "#line default\n" + parts.Types;
                    source._body = parts.Body;
                }
                return source;
            }

            public string Build(BodyForm form)
            {
                var body = form switch
                {
                    BodyForm.Return => Wrap(_body, "return ", ";"),
                    BodyForm.Statement => Wrap(_body, "", ";"),
                    _ => _body
                };
                return $@"{_usings}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using AkerMcp.GodotAdapter;

{_types}
#line default

// Inheriting ScriptGlobals puts selectedObject / Find / FindAll / Create /
// Log directly in scope inside Execute (no `globals.` prefix needed).
public class DynamicScript : ScriptGlobals
{{
    public DynamicScript(StringBuilder output) : base(output) {{ }}

    public object Execute()
    {{
#line {_bodyFirstLine} ""{SnippetFile}""
{body}
#line default
        return null;
    }}
}}

{ScriptCompatibilityShims.ForCurrentRuntime()}";
            }

            // The prefix goes right before the first character and the suffix right after the last,
            // so no line moves.
            private static string Wrap(string body, string prefix, string suffix)
            {
                int start = 0;
                while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
                int end = body.Length;
                while (end > start && char.IsWhiteSpace(body[end - 1])) end--;
                return body.Substring(0, start) + prefix + body.Substring(start, end - start) + suffix + body.Substring(end);
            }

            private static bool IsBareExpression(string body)
            {
                var text = body.Trim();
                if (text.Length == 0) return false;
                var expression = SyntaxFactory.ParseExpression(text, options: ScriptParseOptions);
                return !expression.ContainsDiagnostics && expression.FullSpan.Length == text.Length;
            }

            // Finds the ranges the splitter cut from `rest` with Roslyn, and accepts them only if
            // cutting them reproduces the splitter's body and types exactly.
            private static bool TryLineExactParts(string rest, ScriptSourceParts parts, int usingLines,
                out string types, out string body)
            {
                types = body = "";
                try
                {
                    var tree = CSharpSyntaxTree.ParseText(rest, ScriptParseOptions.WithKind(SourceCodeKind.Script));
                    var ranges = new List<(int start, int end)>();
                    foreach (var member in tree.GetCompilationUnitRoot().Members)
                    {
                        if (member is not BaseTypeDeclarationSyntax && member is not DelegateDeclarationSyntax)
                            continue;

                        // The splitter's cut runs from just after the previous ';' or '}' (or the start)
                        // to the declaration's closing brace, or its ';' when it has no body.
                        var previous = member.GetFirstToken().GetPreviousToken();
                        int start = previous.IsKind(SyntaxKind.None) ? 0 : previous.Span.End;
                        int end = member is BaseTypeDeclarationSyntax declaration
                                  && declaration.CloseBraceToken.IsKind(SyntaxKind.CloseBraceToken)
                                  && !declaration.CloseBraceToken.IsMissing
                            ? declaration.CloseBraceToken.Span.End
                            : member.Span.End;
                        ranges.Add((start, end));
                    }
                    if (ranges.Count == 0) return false;

                    var cutBody = new StringBuilder(rest.Length);
                    var lineBody = new StringBuilder(rest.Length);
                    var typeText = new StringBuilder();
                    var checkTypes = new StringBuilder();
                    int copied = 0;
                    foreach (var (start, end) in ranges)
                    {
                        if (start < copied) return false;
                        cutBody.Append(rest, copied, start - copied);
                        lineBody.Append(rest, copied, start - copied);

                        var range = rest.Substring(start, end - start);
                        lineBody.Append('\n', CountNewlines(range));

                        var declarationText = ScriptSourceSplitter.HoistTypeDeclarations(range).types;
                        checkTypes.Append(declarationText);

                        int first = start;
                        while (first < end && char.IsWhiteSpace(rest[first])) first++;
                        int line = CountNewlines(rest.Substring(0, first)) + 1 + usingLines;
                        typeText.Append("#line ").Append(line.ToString(CultureInfo.InvariantCulture))
                                .Append(" \"").Append(SnippetFile).Append("\"\n")
                                .Append(declarationText);
                        copied = end;
                    }
                    cutBody.Append(rest, copied, rest.Length - copied);
                    lineBody.Append(rest, copied, rest.Length - copied);

                    if (cutBody.ToString() != parts.Body || checkTypes.ToString() != parts.Types)
                        return false;

                    types = typeText.ToString();
                    body = lineBody.ToString();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static int CountNewlines(string text)
            {
                int n = 0;
                foreach (var c in text) if (c == '\n') n++;
                return n;
            }
        }
    }
}
#endif
