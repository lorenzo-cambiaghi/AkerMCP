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

        /// <summary>Set once Log() dropped a line because the output reached its cap.</summary>
        internal bool OutputTruncated { get; private set; }

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
        public void Log(object? message)
        {
            // Stop at the cap here, not only when the result is assembled: a Log() in a
            // long loop would otherwise grow the buffer by megabytes first.
            if (_output.Length >= StrideCodeExecutor.MaxCapturedOutputChars)
            {
                OutputTruncated = true;
                return;
            }
            _output.AppendLine(message?.ToString() ?? "null");
        }

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
        internal const int MaxCapturedOutputChars = 16_000;

        private readonly IMainThreadDispatcher _dispatcher;

        public StrideCodeExecutor(IMainThreadDispatcher dispatcher) => _dispatcher = dispatcher;

        public async Task<CodeExecutionResult> Execute(string code, int timeoutMs = 5000, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Compile off the editor thread: Roslyn needs nothing from it, and a cold
                // compile (references + emit) is long enough to freeze Game Studio. The
                // timeout keeps meaning what it meant: how long the editor thread may take.
                var compiled = await Task.Run(() => Compile(code, ct), ct).ConfigureAwait(false);
                if (compiled.Error != null)
                {
                    sw.Stop();
                    return new CodeExecutionResult { Success = false, Error = compiled.Error, ElapsedMs = sw.Elapsed.TotalMilliseconds };
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var result = await _dispatcher.RunOnMainThread(() => Run(compiled.Image!), cts.Token);
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

        // Snippets are throwaway code: the executor should accept whatever the host compiler can parse,
        // not the conservative default (which lags the language version the project itself is built with).
        private static readonly CSharpParseOptions ScriptParseOptions =
            new CSharpParseOptions(LanguageVersion.Preview);

        private readonly struct CompiledScript
        {
            public CompiledScript(byte[]? image, string? error) { Image = image; Error = error; }
            public byte[]? Image { get; }
            public string? Error { get; }
        }

        private static CompiledScript Compile(string code, CancellationToken ct)
        {
            // The snippet becomes a method body, where `using` directives and type declarations are
            // both illegal. Both are lifted to file scope instead of being refused: see
            // ScriptSourceSplitter for why that is the executor's job and not the caller's.
            var parts = ScriptSourceSplitter.Split(code);

            // The splitter takes the hoisted using lines out of the body and keeps every
            // other line in order, so from the first statement on, body line N is line
            // N + (hoisted usings) of what the caller sent. (A type declared ABOVE a
            // statement shifts the lines below it; that case stays approximate.)
            int firstLine = CountLines(parts.Usings) + 1;
            var references = References();

            // A lone expression (`Find("Player").Transform.Position`) returns its value, as it
            // would in a REPL; a snippet with statements returns what its own `return` says.
            // The wrapping goes on lines of its own, outside the #line-mapped region: the
            // expression may end in a // comment, and its lines must keep their numbers.
            var expression = AsBareExpression(parts.Body);
            if (expression != null)
            {
                var asValue = Emit(BuildSource(parts, "return (object)(", expression, ");", firstLine), references, ct);
                // `Log("x")` without its `;` is an expression too, but void has no value
                // (CS0030): run it as the statement it was meant to be.
                if (asValue.Image != null || !asValue.VoidValue)
                    return new CompiledScript(asValue.Image, asValue.Error);
                var asStatement = Emit(BuildSource(parts, "", expression, ";", firstLine), references, ct);
                return new CompiledScript(asStatement.Image, asStatement.Error);
            }

            var emitted = Emit(BuildSource(parts, "", parts.Body, "", firstLine), references, ct);
            return new CompiledScript(emitted.Image, emitted.Error);
        }

        private static string BuildSource(ScriptSourceParts parts, string prefix, string body, string suffix, int firstLine) => $@"
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
                            {prefix}
#line {firstLine}
{body}
#line default
                            {suffix}
                            return null;
                        }}
                    }}

                    {ScriptCompatibilityShims.ForCurrentRuntime()}";

        private readonly struct EmitResult
        {
            public EmitResult(byte[]? image, string? error, bool voidValue) { Image = image; Error = error; VoidValue = voidValue; }
            public byte[]? Image { get; }
            public string? Error { get; }
            public bool VoidValue { get; }
        }

        private static EmitResult Emit(string source, IReadOnlyList<MetadataReference> references, CancellationToken ct)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, ScriptParseOptions, cancellationToken: ct);
            var compilation = CSharpCompilation.Create(
                "AkerDynamic_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms, cancellationToken: ct);
            if (emit.Success) return new EmitResult(ms.ToArray(), null, false);

            var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            return new EmitResult(null,
                "Compilation error:\n" + string.Join("\n", errors.Select(d => d.ToString())),
                errors.Any(d => d.Id == "CS0030"));
        }

        // The body when it is exactly one expression (trailing whitespace aside), else null.
        // Leading comments stay: they are trivia of the expression and keep the lines aligned.
        private static string? AsBareExpression(string body)
        {
            var text = body.TrimEnd();
            var trimmed = text.TrimStart();
            // A trailing ';' is a statement. A trailing '}' is not decisive: blocks fail the
            // parse below, while `new List<int> { 1, 2 }` is an expression.
            if (trimmed.Length == 0 || trimmed.EndsWith(";", StringComparison.Ordinal))
                return null;
            var expr = SyntaxFactory.ParseExpression(text, 0, ScriptParseOptions, consumeFullText: true);
            return expr.ContainsDiagnostics ? null : text;
        }

        private static int CountLines(string text)
        {
            int n = 0;
            foreach (var c in text) if (c == '\n') n++;
            return n;
        }

        // On the editor thread: load, run, format, unload.
        private static CodeExecutionResult Run(byte[] image)
        {
            var output = new StringBuilder();
            // Collectible, and unloaded after the call: Assembly.Load(byte[]) kept every
            // snippet ever run loaded until Game Studio closed. A snippet that leaves
            // something of itself behind (an event handler, a scheduled task) keeps its
            // context alive until that goes away, and nothing breaks.
            var context = new SnippetLoadContext();
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                var type = assembly.GetType("DynamicScript")!;
                var instance = Activator.CreateInstance(type, output);
                var returnValue = type.GetMethod("Execute")!.Invoke(instance, null);

                var result = new CodeExecutionResult
                {
                    Success = true,
                    // Formatted while the context is still loaded: the value may be a snippet type.
                    ReturnValue = returnValue != null ? FormatReturnValue(returnValue) : null
                };
                result.Output = CapturedOutput(output, (instance as ScriptGlobals)?.OutputTruncated ?? false);
                return result;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}",
                    Output = CapturedOutput(output, false)
                };
            }
            finally
            {
                context.Unload();
            }
        }

        // The declared cap, enforced: the output goes into the tool result, and an
        // unbounded one costs the caller's whole context window.
        private static string? CapturedOutput(StringBuilder output, bool truncatedAtSource)
        {
            if (output.Length == 0) return null;
            var text = output.ToString().TrimEnd();
            bool truncated = truncatedAtSource;
            if (text.Length > MaxCapturedOutputChars)
            {
                text = text.Substring(0, MaxCapturedOutputChars);
                truncated = true;
            }
            return truncated ? text + $"\n... [output truncated at {MaxCapturedOutputChars} characters]" : text;
        }

        /// <summary>
        /// Binds a snippet's references to the copies Game Studio already has loaded, in
        /// whatever load context holds them. The default context alone would miss
        /// AkerMcp.Stride (loaded by path by the startup hook or the source-build loader)
        /// and anything Stride loaded into its own contexts.
        /// </summary>
        private sealed class SnippetLoadContext : AssemblyLoadContext
        {
            public SnippetLoadContext() : base("AkerMcpSnippet", isCollectible: true) { }

            protected override Assembly? Load(AssemblyName name)
            {
                Assembly? sameName = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.IsDynamic) continue;
                    var candidate = a.GetName();
                    if (!string.Equals(candidate.Name, name.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(candidate.FullName, name.FullName, StringComparison.OrdinalIgnoreCase)) return a;
                    sameName = a;   // the last one loaded, as References() picks
                }
                return sameName;
            }
        }

        // --- references -----------------------------------------------------------

        private static readonly object ReferencesGate = new();
        private static readonly Dictionary<string, (DateTime stamp, MetadataReference reference)> ReferenceCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A reference for every assembly loaded from disk. It used to read every one of
        /// them (hundreds of files, tens of MB) on every call; now each file is read once
        /// and reused for as long as its last-write time does not change, so a call only
        /// stats them. Snippets themselves are loaded from memory, have no location, and
        /// so never become references of later snippets.
        /// </summary>
        private static List<MetadataReference> References()
        {
            var projectDir = ProjectDirectory();

            // One file per assembly identity, the last one loaded: two copies of one
            // identity as references is CS1703.
            var byIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                string location;
                try { location = a.Location; } catch { continue; }
                if (string.IsNullOrEmpty(location)) continue;
                byIdentity[a.FullName ?? location] = location;
            }

            lock (ReferencesGate)
            {
                var refs = new List<MetadataReference>(byIdentity.Count);
                foreach (var location in byIdentity.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    DateTime stamp;
                    try { stamp = File.GetLastWriteTimeUtc(location); } catch { continue; }

                    if (!ReferenceCache.TryGetValue(location, out var cached) || cached.stamp != stamp)
                    {
                        try { cached = (stamp, CreateReference(location, projectDir)); }
                        catch { continue; /* unreadable assembly */ }
                        ReferenceCache[location] = cached;
                    }
                    refs.Add(cached.reference);
                }
                return refs;
            }
        }

        // From a copy in memory for the project's own outputs: a file-backed reference maps
        // the DLL and holds it, and the next `dotnet build` of the game must overwrite it.
        // Editor, runtime and NuGet assemblies never change under a running editor, so
        // those are mapped (no copy of them kept in memory).
        private static MetadataReference CreateReference(string location, string? projectDir)
        {
            if (projectDir != null && location.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
                return MetadataReference.CreateFromImage(File.ReadAllBytes(location));
            return MetadataReference.CreateFromFile(location);
        }

        private static string? ProjectDirectory()
        {
            try
            {
                var solution = StrideSceneBridge.Session?.SolutionPath;
                var dir = solution != null ? Path.GetDirectoryName(solution.ToOSPath()) : null;
                return string.IsNullOrEmpty(dir) ? null : dir!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch
            {
                return null;
            }
        }

        // --- formatting -----------------------------------------------------------

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
                    items.Add(item != null ? Invariant(item) : "null");
                    if (items.Count >= 20) { items.Add("..."); break; }
                }
                return $"[{string.Join(", ", items)}]";
            }
            return Invariant(value);
        }

        // The server reads these values back as numbers (sample_state / assert_state): a
        // float must come back as "0.5" whatever the editor's culture, never "0,5".
        private static string Invariant(object value)
            => value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "null";
    }
}
