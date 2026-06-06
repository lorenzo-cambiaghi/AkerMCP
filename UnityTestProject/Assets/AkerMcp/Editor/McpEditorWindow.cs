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

        // Lifecycle (assembly reload, editor quit) is handled centrally
        // by UnityMcpLifecycle so cleanup runs even when this window is closed.

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

            // Toggle Auto-Restart
            bool autoRestart = EditorPrefs.GetBool("AkerMcp_AutoRestartEnabled", true);
            bool newAutoRestart = EditorGUILayout.ToggleLeft("Auto-start plugin on Unity load/compile", autoRestart);
            if (newAutoRestart != autoRestart)
            {
                EditorPrefs.SetBool("AkerMcp_AutoRestartEnabled", newAutoRestart);
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
                "1. Click 'Start' above.\n" +
                "2. Download the standalone MCP server from the GitHub 'Build/' folder.\n" +
                "3. Configure your AI client (Claude, Cursor, Windsurf, etc.) to point to the downloaded executable.",
                MessageType.Info);

            EditorGUILayout.Space(5);
            if (GUILayout.Button("View Setup Instructions on GitHub", GUILayout.Height(25)))
            {
                Application.OpenURL("https://github.com/lorenzo-cambiaghi/AkerMCP#connecting-an-ai-client");
            }
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }
    }
}
