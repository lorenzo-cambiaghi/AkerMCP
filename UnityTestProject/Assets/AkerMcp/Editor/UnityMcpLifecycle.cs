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

            // Riavvio automatico schedulato all'avvio di Unity e dopo ogni ricompilazione
            EditorApplication.delayCall += AutoStartIfEnabled;
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
