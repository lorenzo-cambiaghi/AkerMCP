
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    public class UnityCompilationSupport : ICompilationSupport
    {
        // A successful compile triggers a domain reload that wipes this instance.
        // The last result is persisted in SessionState so get_compile_errors right
        // after the reload still reports that compile's warnings/outcome.
        private const string SessionKey = "AkerMcp_LastCompileResult";

        private readonly List<CompileMessage> _messages = new List<CompileMessage>();
        private bool _isCompiling;
        private bool _lastCompileSucceeded = true;
        private DateTime _lastCompileTime = DateTime.MinValue;
        private bool _hooked;
        // The editor says the scripts do not compile, but no compile that this plugin heard produced the errors.
        private bool _failedUnseen;

        public UnityCompilationSupport()
        {
            RestoreFromSession();
            ReconcileWithEditor();
            HookEvents();
        }

        /// <summary>
        /// The events only report compiles that run while this instance listens. A project opened with compile errors
        /// was compiled before this assembly loaded, and SessionState starts empty in a new editor session: the report
        /// read "Compilation SUCCEEDED at never: 0 error(s)" for a project that does not compile. The editor's own
        /// verdict on the scripts as they stand wins over a remembered success.
        /// </summary>
        private void ReconcileWithEditor()
        {
            if (!EditorUtility.scriptCompilationFailed || !_lastCompileSucceeded) return;

            _failedUnseen = true;
            _lastCompileSucceeded = false;
            _messages.Clear();
            // File doubles as the message prefix, so the report prints the sentence alone instead of "(0,0): error:".
            _messages.Add(new CompileMessage
            {
                Type = CompileMessageType.Error,
                File = "AkerMcp",
                Message = "AkerMcp: the editor reports script compile errors from a compile that ran before the " +
                          "plugin started, so their text was not captured. refresh_scripts recompiles and reports them " +
                          "(the Unity Console shows them too).",
            });
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
            // Incremental: import changed assets and compile only the affected
            // assemblies — same effect as giving the editor focus with Auto Refresh on.
            // (RequestScriptCompilation/CleanBuildCache would force a full rebuild of
            // every assembly: tens of seconds of editor downtime per micro-change.)
            AssetDatabase.Refresh();

            // Errors that predate the plugin come back only from a compile that runs now: with nothing changed on
            // disk, the refresh above compiles nothing and the report would stay a placeholder.
            if (_failedUnseen && !EditorApplication.isCompiling)
            {
                CompilationPipeline.RequestScriptCompilation();
                Debug.Log("[AkerMcp] Script compilation requested: the current compile errors predate the plugin.");
                return;
            }

            Debug.Log("[AkerMcp] Asset refresh requested (incremental script compile).");
        }

        public CompilationStatus GetCompilationStatus()
        {
            return new CompilationStatus
            {
                IsCompiling = _isCompiling || EditorApplication.isCompiling,
                IsImporting = EditorApplication.isUpdating,
                LastCompileSucceeded = _lastCompileSucceeded,
                ErrorCount = _messages.Count(m => m.Type == CompileMessageType.Error),
                WarningCount = _messages.Count(m => m.Type == CompileMessageType.Warning),
                LastCompileTime = _failedUnseen
                    ? "unknown (before the plugin started)"
                    : _lastCompileTime == DateTime.MinValue
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
            // The SessionState copy too: it is what the next instance restores after a domain reload, and it brought
            // the cleared messages back.
            SaveToSession();
        }

        private void OnCompilationStarted(object context)
        {
            _isCompiling = true;
            _failedUnseen = false;
            _messages.Clear();
        }

        private void OnCompilationFinished(object context)
        {
            _isCompiling = false;
            _lastCompileTime = DateTime.Now;
            _lastCompileSucceeded = !_messages.Any(m => m.Type == CompileMessageType.Error);

            var errorCount = _messages.Count(m => m.Type == CompileMessageType.Error);
            var warningCount = _messages.Count(m => m.Type == CompileMessageType.Warning);

            SaveToSession();

            if (errorCount > 0)
                Debug.LogWarning($"[AkerMcp] Compilation finished with {errorCount} error(s), {warningCount} warning(s).");
            else
                Debug.Log($"[AkerMcp] Compilation succeeded. {warningCount} warning(s).");
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

        private void SaveToSession()
        {
            try
            {
                var snapshot = new SessionSnapshot
                {
                    Succeeded = _lastCompileSucceeded,
                    CompileTimeTicks = _lastCompileTime.Ticks,
                    Messages = _messages.ToList()
                };
                SessionState.SetString(SessionKey, System.Text.Json.JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AkerMcp] Could not persist compile result: {ex.Message}");
            }
        }

        private void RestoreFromSession()
        {
            try
            {
                var json = SessionState.GetString(SessionKey, "");
                if (string.IsNullOrEmpty(json)) return;

                var snapshot = System.Text.Json.JsonSerializer.Deserialize<SessionSnapshot>(json);
                if (snapshot == null) return;

                _lastCompileSucceeded = snapshot.Succeeded;
                _lastCompileTime = new DateTime(snapshot.CompileTimeTicks);
                _messages.Clear();
                if (snapshot.Messages != null)
                    _messages.AddRange(snapshot.Messages);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AkerMcp] Could not restore compile result: {ex.Message}");
            }
        }

        private class SessionSnapshot
        {
            public bool Succeeded { get; set; }
            public long CompileTimeTicks { get; set; }
            public List<CompileMessage> Messages { get; set; } = new List<CompileMessage>();
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
