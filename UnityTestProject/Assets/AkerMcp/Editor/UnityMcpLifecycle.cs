using UnityEditor;

namespace AkerMcp.Unity.Editor
{
    [InitializeOnLoad]
    internal static class UnityMcpLifecycle
    {
        static UnityMcpLifecycle()
        {
            EditorApplication.quitting += StopIfRunning;
            AssemblyReloadEvents.beforeAssemblyReload += StopIfRunning;

            // Riavvio automatico al primo tick dell'editor loop. NON delayCall:
            // delayCall aspetta un repaint della GUI, che con l'editor sfocato non
            // arriva mai — il plugin resterebbe fermo dopo ogni ricompilazione in
            // background finché l'utente non clicca su Unity. update ticka comunque.
            EditorApplication.update += AutoStartOnce;
        }

        private static void AutoStartOnce()
        {
            EditorApplication.update -= AutoStartOnce;
            AutoStartIfEnabled();
        }

        private static void StopIfRunning()
        {
            if (UnityMcpPlugin.IsRunning)
                UnityMcpPlugin.Instance.Stop();
        }

        private static void AutoStartIfEnabled()
        {
            bool autoRestartEnabled = EditorPrefs.GetBool("AkerMcp_AutoRestartEnabled", true);
            if (autoRestartEnabled && !UnityMcpPlugin.IsRunning)
            {
                UnityMcpPlugin.Instance.Start();
                UnityEngine.Debug.Log("[AkerMcp] Plugin auto-started.");
            }
        }
    }
}
