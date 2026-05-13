#nullable enable
#if UNITY_EDITOR

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

    public class DynamicEvaluator : ICodeExecutor
    {
        private ScriptState<object>? _state;
        private readonly ScriptOptions _options;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly StringBuilder _outputCapture = new StringBuilder();

        public DynamicEvaluator(IMainThreadDispatcher dispatcher)
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
                    Error = ex.Message,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
        }

        private CodeExecutionResult ExecuteInternal(string code)
        {
            try
            {
                var globals = new ScriptGlobals();

                if (_state != null)
                {
                    _state = _state.ContinueWithAsync<object>(code).GetAwaiter().GetResult();
                }
                else
                {
                    _state = CSharpScript.RunAsync<object>(
                        code,
                        _options,
                        globals,
                        typeof(ScriptGlobals)
                    ).GetAwaiter().GetResult();
                }

                var returnValue = _state.ReturnValue;
                string? returnStr = null;

                if (returnValue != null)
                {
                    returnStr = FormatReturnValue(returnValue);
                }

                return new CodeExecutionResult
                {
                    Success = true,
                    ReturnValue = returnStr,
                    Output = _outputCapture.Length > 0 ? _outputCapture.ToString() : null
                };
            }
            catch (CompilationErrorException ex)
            {
                _state = null;
                var errors = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"Compilation error:\n{errors}"
                };
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"{inner.GetType().Name}: {inner.Message}"
                };
            }
        }

        private ScriptOptions BuildScriptOptions()
        {
            var assemblies = new List<Assembly>();
            var imports = new List<string>
            {
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "UnityEngine",
                "UnityEditor"
            };

            // Core .NET
            assemblies.Add(typeof(object).Assembly);
            assemblies.Add(typeof(Enumerable).Assembly);

            // Unity assemblies
            AddAssemblySafe(assemblies, typeof(GameObject));      // UnityEngine.CoreModule
            AddAssemblySafe(assemblies, typeof(Transform));
            AddAssemblySafe(assemblies, typeof(Rigidbody));       // UnityEngine.PhysicsModule
            AddAssemblySafe(assemblies, typeof(Camera));
            AddAssemblySafe(assemblies, typeof(Light));
            AddAssemblySafe(assemblies, typeof(MeshRenderer));
            AddAssemblySafe(assemblies, typeof(AudioSource));
            AddAssemblySafe(assemblies, typeof(Canvas));
            AddAssemblySafe(assemblies, typeof(Animator));
            AddAssemblySafe(assemblies, typeof(UnityEditor.Editor)); // UnityEditor
            AddAssemblySafe(assemblies, typeof(Selection));
            AddAssemblySafe(assemblies, typeof(AssetDatabase));
            AddAssemblySafe(assemblies, typeof(EditorApplication));
            AddAssemblySafe(assemblies, typeof(Undo));

            // User assemblies (project scripts)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                var name = asm.GetName().Name ?? "";
                if (name == "Assembly-CSharp" || name == "Assembly-CSharp-Editor")
                    assemblies.Add(asm);
            }

            var distinct = assemblies.Distinct().ToArray();

            return ScriptOptions.Default
                .WithReferences(distinct)
                .WithImports(imports)
                .WithAllowUnsafe(false);
        }

        private static void AddAssemblySafe(List<Assembly> list, Type type)
        {
            try { list.Add(type.Assembly); }
            catch { }
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

        public void ResetState()
        {
            _state = null;
        }
    }
}

#endif
