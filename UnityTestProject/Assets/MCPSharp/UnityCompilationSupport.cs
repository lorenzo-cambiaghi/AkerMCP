#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using MCPSharp.Shared.Abstraction;

namespace MCPSharp.Unity
{
    public class UnityCompilationSupport : ICompilationSupport
    {
        private readonly List<CompileMessage> _messages = new List<CompileMessage>();
        private bool _isCompiling;
        private bool _lastCompileSucceeded = true;
        private DateTime _lastCompileTime = DateTime.MinValue;
        private bool _hooked;

        public UnityCompilationSupport()
        {
            HookEvents();
        }

        private void HookEvents()
        {
            if (_hooked) return;
            _hooked = true;

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        public void RequestRecompile()
        {
            _messages.Clear();
            _isCompiling = true;

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);

            Debug.Log("[MCPSharp] Script recompilation requested.");
        }

        public CompilationStatus GetCompilationStatus()
        {
            return new CompilationStatus
            {
                IsCompiling = _isCompiling || EditorApplication.isCompiling,
                LastCompileSucceeded = _lastCompileSucceeded,
                ErrorCount = _messages.Count(m => m.Type == CompileMessageType.Error),
                WarningCount = _messages.Count(m => m.Type == CompileMessageType.Warning),
                LastCompileTime = _lastCompileTime == DateTime.MinValue
                    ? "never"
                    : _lastCompileTime.ToString("HH:mm:ss")
            };
        }

        public IEnumerable<CompileMessage> GetCompileMessages()
        {
            return _messages.ToList();
        }

        public void ClearCompileMessages()
        {
            _messages.Clear();
        }

        private void OnCompilationStarted(object context)
        {
            _isCompiling = true;
            _messages.Clear();
        }

        private void OnCompilationFinished(object context)
        {
            _isCompiling = false;
            _lastCompileTime = DateTime.Now;
            _lastCompileSucceeded = !_messages.Any(m => m.Type == CompileMessageType.Error);

            var errorCount = _messages.Count(m => m.Type == CompileMessageType.Error);
            var warningCount = _messages.Count(m => m.Type == CompileMessageType.Warning);

            if (errorCount > 0)
                Debug.LogWarning($"[MCPSharp] Compilation finished with {errorCount} error(s), {warningCount} warning(s).");
            else
                Debug.Log($"[MCPSharp] Compilation succeeded. {warningCount} warning(s).");
        }

        private void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error || msg.type == CompilerMessageType.Warning)
                {
                    _messages.Add(new CompileMessage
                    {
                        Type = msg.type == CompilerMessageType.Error
                            ? CompileMessageType.Error
                            : CompileMessageType.Warning,
                        Message = msg.message,
                        File = msg.file ?? "",
                        Line = msg.line,
                        Column = msg.column
                    });
                }
            }
        }

        public void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
        }
    }
}
#endif
