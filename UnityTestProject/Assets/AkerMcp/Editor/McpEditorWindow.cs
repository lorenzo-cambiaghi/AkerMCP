#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace AkerMcp.Unity.Editor
{
    public class McpEditorWindow : EditorWindow
    {
        [MenuItem("Window/AkerMcp")]
        public static void ShowWindow()
        {
            GetWindow<McpEditorWindow>("AkerMcp");
        }

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            if (UnityMcpPlugin.IsRunning)
                UnityMcpPlugin.Instance.Stop();
        }

        private void OnBeforeAssemblyReload()
        {
            if (UnityMcpPlugin.IsRunning)
                UnityMcpPlugin.Instance.Stop();
        }

        private void OnGUI()
        {
            GUILayout.Label("AkerMcp — Game Engine MCP Bridge", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            var isRunning = UnityMcpPlugin.IsRunning;

            // Status
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Status:", GUILayout.Width(60));
            var statusStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = isRunning ? Color.green : Color.gray }
            };
            GUILayout.Label(isRunning ? "Running" : "Stopped", statusStyle);
            EditorGUILayout.EndHorizontal();

            if (isRunning)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Pipe:", GUILayout.Width(60));
                EditorGUILayout.SelectableLabel(UnityMcpPlugin.Instance.CurrentPipeName,
                    GUILayout.Height(18));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(10);

            // Start/Stop button
            if (!isRunning)
            {
                if (GUILayout.Button("Start AkerMcp Plugin", GUILayout.Height(35)))
                {
                    UnityMcpPlugin.Instance.Start();
                }
            }
            else
            {
                if (GUILayout.Button("Stop AkerMcp Plugin", GUILayout.Height(35)))
                {
                    UnityMcpPlugin.Instance.Stop();
                }
            }

            EditorGUILayout.Space(15);
            EditorGUILayout.HelpBox(
                "How to connect:\n" +
                "1. Click 'Start' above\n" +
                "2. Run: dotnet run --project Server\n" +
                "   (from the AkerMcp folder)\n" +
                "3. The server auto-discovers this plugin\n\n" +
                "Claude Desktop config:\n" +
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"game-engine\": {\n" +
                "      \"command\": \"dotnet\",\n" +
                "      \"args\": [\"run\", \"--project\", \"/path/to/AkerMcp/Server\"]\n" +
                "    }\n" +
                "  }\n" +
                "}",
                MessageType.Info);
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }
    }
}
#endif
