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
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using UnityEngine;
using UnityEditor;
using AkerMcp.Shared.Abstraction;
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
        private readonly ScriptOptions _options;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly StringBuilder _outputCapture = new StringBuilder();

        public DynamicEvaluatorV2(IMainThreadDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _options = BuildScriptOptions();
        }

        public async Task<CodeExecutionResult> Execute(string code, int timeoutMs = 5000, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                var result = await _dispatcher.RunOnMainThread(() =>
                {
                    _outputCapture.Clear();
                    return ExecuteInternal(code);
                }, cts.Token);

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
                    Error = $"Execution timed out after {timeoutMs}ms",
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
            try
            {
                var assemblyName = "AkerDynamic_" + Guid.NewGuid().ToString("N");
                var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText($@"
                    using System;
                    using System.Collections.Generic;
                    using System.Linq;
                    using System.Text;
                    using UnityEngine;
                    using UnityEditor;
                    using AkerMcp.Unity;
                    using UnityEngine.Rendering;

                    public class DynamicScript 
                    {{
                        public object Execute(ScriptGlobals globals) 
                        {{
                            {code}
                            return null;
                        }}
                    }}");

                var references = _options.MetadataReferences;
                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                    assemblyName,
                    new[] { syntaxTree },
                    references,
                    new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

                using (var ms = new MemoryStream())
                {
                    var emitResult = compilation.Emit(ms);
                    if (!emitResult.Success)
                    {
                        var errors = string.Join("\n", emitResult.Diagnostics
                            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                            .Select(d => d.ToString()));
                        return new CodeExecutionResult { Success = false, Error = "Compilation error:\n" + errors };
                    }

                    var assembly = Assembly.Load(ms.ToArray());
                    var type = assembly.GetType("DynamicScript");
                    if (type == null) return new CodeExecutionResult { Success = false, Error = "Failed to find DynamicScript class" };

                    var instance = Activator.CreateInstance(type);
                    var method = type.GetMethod("Execute");
                    if (method == null) return new CodeExecutionResult { Success = false, Error = "Failed to find Execute method" };

                    var globals = new ScriptGlobals();
                    var returnValue = method.Invoke(instance, new object[] { globals });

                    return new CodeExecutionResult
                    {
                        Success = true,
                        ReturnValue = returnValue != null ? FormatReturnValue(returnValue) : null
                    };
                }
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

        private ScriptOptions BuildScriptOptions()
        {
            var imports = new List<string>
            {
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "UnityEngine",
                "UnityEditor"
            };

            // Include ALL non-dynamic assemblies currently loaded in the AppDomain.
            // This automatically picks up URP, HDRP, Input System, TextMeshPro,
            // Cinemachine, user scripts, and any other UPM package — without
            // maintaining a hardcoded list.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Where(a =>
                {
                    try { _ = a.Location; return !string.IsNullOrEmpty(a.Location); }
                    catch { return false; }
                })
                .Distinct()
                .ToArray();

            return ScriptOptions.Default
                .WithReferences(assemblies)
                .WithImports(imports)
                .WithAllowUnsafe(false);
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
                    items.Add(item?.ToString() ?? "null");
                    if (items.Count >= 20) { items.Add("..."); break; }
                }
                return $"[{string.Join(", ", items)}]";
            }

            return value.ToString() ?? "null";
        }
    }
}
