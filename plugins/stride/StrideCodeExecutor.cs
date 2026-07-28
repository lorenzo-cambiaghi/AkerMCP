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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Scripting;
using Stride.Engine;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Globals available inside `execute` scripts. Mirrors the Unity/Godot surface
    /// (Find / FindAll / Log / root access) in Stride terms, over the live scene.
    /// Reads/queries and engine-API calls work; for persistent, undoable edits use
    /// set_property/create/delete (which go through Quantum) rather than mutating
    /// game-side entities here.
    /// </summary>
    public class ScriptGlobals
    {
        private readonly StringBuilder _output;

        public ScriptGlobals(StringBuilder output) => _output = output;

        public IEnumerable<Entity> RootEntities => StrideSceneBridge.GetRootEntities();

        public Entity? Find(string name)
        {
            foreach (var e in AllEntities())
                if (e.Name == name) return e;
            return null;
        }

        public T[] FindAll<T>() where T : EntityComponent
        {
            var list = new List<T>();
            foreach (var e in AllEntities())
                foreach (var c in e.Components)
                    if (c is T t) list.Add(t);
            return list.ToArray();
        }

        // No global log hook in Stride, so Log() is the way to surface text.
        public void Log(object? message) => _output.AppendLine(message?.ToString() ?? "null");

        private IEnumerable<Entity> AllEntities()
        {
            foreach (var root in RootEntities)
                foreach (var e in Traverse(root)) yield return e;
        }

        private static IEnumerable<Entity> Traverse(Entity entity)
        {
            yield return entity;
            foreach (var child in entity.Transform.Children)
                foreach (var d in Traverse(child.Entity)) yield return d;
        }
    }

    public class StrideCodeExecutor : ICodeExecutor
    {
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly StringBuilder _outputCapture = new();
        private const int MaxCapturedOutputChars = 16_000;

        public StrideCodeExecutor(IMainThreadDispatcher dispatcher) => _dispatcher = dispatcher;

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
                var assemblyName = "AkerDynamic_" + Guid.NewGuid().ToString("N");
                // The snippet becomes a method body, where `using` directives and type declarations are
                // both illegal. Both are lifted to file scope instead of being refused: see
                // ScriptSourceSplitter for why that is the executor's job and not the caller's.
                var parts = ScriptSourceSplitter.Split(code);
                var source = $@"
                    {parts.Usings}
                    using System;
                    using System.Collections.Generic;
                    using System.Linq;
                    using System.Text;
                    using Stride.Engine;
                    using Stride.Core.Mathematics;
                    using AkerMcp.StrideAdapter;

                    {parts.Types}

                    // Inheriting ScriptGlobals puts Find / FindAll / Log / RootEntities
                    // directly in scope inside Execute (no `globals.` prefix needed).
                    public class DynamicScript : ScriptGlobals
                    {{
                        public DynamicScript(StringBuilder output) : base(output) {{ }}

                        public object Execute()
                        {{
                            {parts.Body}
                            return null;
                        }}
                    }}

                    {ScriptCompatibilityShims.ForCurrentRuntime()}";
                var syntaxTree = CSharpSyntaxTree.ParseText(source, ScriptParseOptions);

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
                var instance = Activator.CreateInstance(type, _outputCapture);
                var method = type.GetMethod("Execute")!;
                var returnValue = method.Invoke(instance, null);

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
            if (value is Entity e) return $"Entity(\"{e.Name}\")";
            if (value is EntityComponent c) return $"{c.GetType().Name} on \"{c.Entity?.Name}\"";

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
