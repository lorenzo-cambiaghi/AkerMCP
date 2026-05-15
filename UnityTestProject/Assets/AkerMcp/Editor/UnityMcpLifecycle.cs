#if UNITY_EDITOR
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
        }

        private static void StopIfRunning()
        {
            if (UnityMcpPlugin.IsRunning)
                UnityMcpPlugin.Instance.Stop();
        }
    }
}
#endif
