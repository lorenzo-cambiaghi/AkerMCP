#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using AkerMcp.Shared.Abstraction;

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
            _output.AppendLine(text);
        }

        private static void Collect<T>(Node node, List<T> list) where T : Node
        {
            if (node is T t) list.Add(t);
            foreach (Node child in node.GetChildren()) Collect(child, list);
        }
    }

    public class GodotCodeExecutor : ICodeExecutor
    {
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly StringBuilder _outputCapture = new();
        private const int MaxCapturedOutputChars = 16_000;

        public GodotCodeExecutor(IMainThreadDispatcher dispatcher) => _dispatcher = dispatcher;

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

        private CodeExecutionResult CompileAndRun(string code)
        {
            try
            {
                var assemblyName = "AkerDynamic_" + Guid.NewGuid().ToString("N");
                var syntaxTree = CSharpSyntaxTree.ParseText($@"
                    using System;
                    using System.Collections.Generic;
                    using System.Linq;
                    using System.Text;
                    using Godot;
                    using AkerMcp.GodotAdapter;

                    public class DynamicScript
                    {{
                        public object Execute(ScriptGlobals globals)
                        {{
                            {code}
                            return null;
                        }}
                    }}");

                var compilation = CSharpCompilation.Create(
                    assemblyName,
                    new[] { syntaxTree },
                    BuildReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                using var ms = new MemoryStream();
                var emit = compilation.Emit(ms);
                if (!emit.Success)
                {
                    var errors = string.Join("\n", emit.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString()));
                    return new CodeExecutionResult { Success = false, Error = "Compilation error:\n" + errors };
                }

                var assembly = Assembly.Load(ms.ToArray());
                var type = assembly.GetType("DynamicScript")!;
                var instance = Activator.CreateInstance(type);
                var method = type.GetMethod("Execute")!;

                var globals = new ScriptGlobals(_outputCapture);
                var returnValue = method.Invoke(instance, new object?[] { globals });

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

        private static List<MetadataReference> BuildReferences()
        {
            // Reference every loaded, file-backed assembly: GodotSharp, the project
            // assembly, AkerMcp.* and any NuGet deps. Load from an in-memory image so
            // we never hold a file lock on assemblies Godot may rebuild/hot-reload.
            var refs = new List<MetadataReference>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                string loc;
                try { loc = a.Location; } catch { continue; }
                if (string.IsNullOrEmpty(loc)) continue;
                try { refs.Add(MetadataReference.CreateFromImage(File.ReadAllBytes(loc))); }
                catch { /* skip unreadable assembly */ }
            }
            return refs;
        }

        private static string FormatReturnValue(object value)
        {
            if (value is string s) return s;
            if (value is Node node) return $"{node.GetType().Name}(\"{node.Name}\")";

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    items.Add(item?.ToString() ?? "null");
                    if (items.Count >= 20) { items.Add("..."); break; }
                }
                return $"[{string.Join(", ", items)}]";
            }

            return value.ToString() ?? "null";
        }
    }
}
