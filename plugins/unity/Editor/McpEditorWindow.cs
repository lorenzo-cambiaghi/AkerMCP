#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Setup;
using UnityEditor;
using UnityEngine;

namespace AkerMcp.Unity.Editor
{
    /// <summary>
    /// Window > AkerMcp. The simple part is what a person needs: is it running, which AI clients are connected, one
    /// button per client, one to update the plugin. Everything else sits under Advanced, collapsed.
    ///
    /// Connect needs nothing installed: it downloads the server of this plugin's version from the GitHub release into
    /// the per-user folder (<see cref="ServerInstall"/>) and points the client config at it, with a backup of the file.
    /// When the plugin is updated, the clients still start the old server and their button turns into Update.
    ///
    /// The window repaints only when what it shows changes: client configs are re-read when a file's timestamp moves,
    /// not on a timer, and the connection count is compared before repainting.
    /// </summary>
    public class McpEditorWindow : EditorWindow
    {
        /// <summary>Stamped with the release version; publish-release.ps1 checks it.</summary>
        internal const string PluginVersion = "1.7.0";

        internal const string AllowExecutePref = "AkerMcp_AllowExecute";
        private const string AutoRestartPref = "AkerMcp_AutoRestartEnabled";
        private const string ProfilePref = "AkerMcp_SetupProfile";
        private const string OutdatedNoticeSessionKey = "AkerMcp_OutdatedNoticeShown";

        private const string LatestReleaseApiUrl = "https://api.github.com/repos/" + ServerInstall.Repository + "/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/" + ServerInstall.Repository + "/releases/latest";
        private const string DocsUrl = "https://github.com/" + ServerInstall.Repository + "/blob/main/docs/setup.md#connecting-an-ai-client";

        private static readonly string[] ProfileNames = { "standard", "core", "full" };
        private static readonly GUIContent[] ProfileLabels =
        {
            new GUIContent("standard (27 tools, default)"),
            new GUIContent("core (14 tools, fewest tokens)"),
            new GUIContent("full (42 tools: sprites, audio, playtest, builds)"),
        };

        private enum RowAction { None, Connect, Update, Apply, Disconnect, Replace, Repair }

        private sealed class ClientRow
        {
            public AiClientDefinition Client = null!;
            public ClientConfigStatus Status = null!;
            public AiConnection Connection = null!;
        }

        private bool _showAdvanced;
        private bool _showAllClients;
        private Vector2 _scroll;

        private List<ClientRow> _rows = new List<ClientRow>();
        private string _configSignature = "";
        private double _nextSignatureCheck;

        private bool _wasRunning;
        private int _shownConnections = -1;

        private string? _banner;
        private MessageType _bannerType = MessageType.Info;

        // Server download (Connect / Update)
        private bool _downloading;
        private float _downloadProgress;
        private int _shownProgressPercent = -1;
        private readonly List<AiClientDefinition> _waitingForServer = new List<AiClientDefinition>();

        // Plugin update
        private bool _checkingUpdate;
        private string? _updateMessage;
        private string? _updateTag;
        private string? _updateNotesUrl;
        private bool _importingUpdate;

        private GUIStyle? _okStyle, _warnStyle, _errorStyle, _mutedStyle;

        [MenuItem("Window/AkerMcp")]
        public static void ShowWindow()
        {
            var win = GetWindow<McpEditorWindow>("AkerMcp");
            win.minSize = new Vector2(360, 320);
        }

        private void OnEnable() => RescanClients();

        private void OnFocus()
        {
            // Coming back from the AI client's settings: re-read only if a file actually changed.
            if (ConfigSignature() != _configSignature) RescanClients();
        }

        private void OnInspectorUpdate()
        {
            var dirty = false;

            var running = UnityMcpPlugin.IsRunning;
            var connections = running ? UnityMcpPlugin.Instance.ActiveConnectionCount : 0;
            if (running != _wasRunning || connections != _shownConnections)
            {
                _wasRunning = running;
                _shownConnections = connections;
                dirty = true;
            }

            if (EditorApplication.timeSinceStartup >= _nextSignatureCheck)
            {
                _nextSignatureCheck = EditorApplication.timeSinceStartup + 1.0;
                if (ConfigSignature() != _configSignature)
                {
                    RescanClients();
                    dirty = true;
                }
            }

            if (_downloading)
            {
                var percent = Mathf.FloorToInt(_downloadProgress * 100);
                if (percent != _shownProgressPercent)
                {
                    _shownProgressPercent = percent;
                    dirty = true;
                }
            }

            if (dirty) Repaint();
        }

        private void OnGUI()
        {
            EnsureStyles();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawStatus();
            EditorGUILayout.Space(10);
            DrawClients();
            EditorGUILayout.Space(10);
            DrawPluginUpdate();
            EditorGUILayout.Space(10);
            DrawAdvanced();

            EditorGUILayout.EndScrollView();
        }

        // ---- Status --------------------------------------------------------------------------------------------

        private void DrawStatus()
        {
            var running = UnityMcpPlugin.IsRunning;
            var connections = running ? UnityMcpPlugin.Instance.ActiveConnectionCount : 0;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("AkerMCP", EditorStyles.boldLabel, GUILayout.Width(70));
            if (!running)
                GUILayout.Label("Stopped", _mutedStyle);
            else if (connections > 0)
                GUILayout.Label(connections == 1 ? "Running, 1 AI client connected" : $"Running, {connections} AI clients connected", _okStyle);
            else
                GUILayout.Label("Running, waiting for an AI client", _warnStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(running ? "Stop" : "Start", GUILayout.Width(70)))
            {
                if (running) UnityMcpPlugin.Instance.Stop();
                else UnityMcpPlugin.Instance.Start();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---- AI clients ----------------------------------------------------------------------------------------

        private void DrawClients()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Connect your AI", EditorStyles.boldLabel);

            if (_downloading)
            {
                var rect = GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, _downloadProgress, $"Downloading the AkerMCP server {PluginVersion}... {Mathf.FloorToInt(_downloadProgress * 100)}%");
                EditorGUILayout.Space(2);
            }

            var rows = _showAllClients ? _rows : _rows.Where(r => r.Client.IsInstalled || r.Connection.Kind != ConnectionKind.NotConfigured).ToList();
            if (rows.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No AI client found on this computer (" + string.Join(", ", AiClientCatalog.All.Select(c => c.DisplayName)) + "). " +
                    "Install one, or click 'Show all clients' to set one up anyway.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUI.DisabledScope(_downloading))
            {
                foreach (var row in rows)
                    DrawClientRow(row);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_showAllClients ? "Show installed clients only" : "Show all clients", EditorStyles.miniButton))
                _showAllClients = !_showAllClients;
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_banner))
                EditorGUILayout.HelpBox(_banner, _bannerType);

            EditorGUILayout.EndVertical();
        }

        private void DrawClientRow(ClientRow row)
        {
            var (text, style, tooltip) = Describe(row);
            var action = ActionFor(row);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(row.Client.DisplayName, row.Client.ConfigPath), GUILayout.Width(125));
            GUILayout.Label(new GUIContent(text, tooltip), style, GUILayout.ExpandWidth(true), GUILayout.MinWidth(60));
            if (action != RowAction.None)
            {
                var (label, hint) = Label(action);
                if (GUILayout.Button(new GUIContent(label, hint), GUILayout.Width(80)))
                    Run(row, action);
            }
            else if (row.Connection.Kind == ConnectionKind.Unreadable)
            {
                if (GUILayout.Button(new GUIContent("Show file", "Open the folder of the file to fix it by hand."), GUILayout.Width(80)))
                    EditorUtility.RevealInFinder(row.Client.ConfigPath);
            }
            EditorGUILayout.EndHorizontal();
        }

        private (string text, GUIStyle style, string tooltip) Describe(ClientRow row)
        {
            var c = row.Connection;
            var projects = row.Status.ProjectEntries;
            var projectNote = projects.Count == 0
                ? ""
                : $"\n{projects.Count} project(s) have their own entry, which wins inside them:\n" + string.Join("\n", projects.Take(5));

            switch (c.Kind)
            {
                case ConnectionKind.NotConfigured:
                    return (projects.Count > 0 ? $"Set per project ({projects.Count})" : "Not connected",
                        _mutedStyle!, "Connect writes the 'game-engine' entry in " + row.Client.ConfigPath + projectNote);
                case ConnectionKind.Installed when c.RunsVersion(PluginVersion):
                    return ("Connected" + (c.Profile != "standard" ? $" ({c.Profile})" : ""), _okStyle!,
                        $"Starts the AkerMCP server {c.ServerVersion}, profile {c.Profile}." + projectNote);
                case ConnectionKind.Installed:
                    return ($"Uses server {c.ServerVersion}", _warnStyle!,
                        $"This plugin is {PluginVersion}. Update points {row.Client.DisplayName} at server {PluginVersion}." + projectNote);
                // What `npx -y aker-mcp@latest setup` and `aker-mcp setup` write, as the README tells people to: connected,
                // not "your own setup" — a reader who followed the README must not be offered to replace it.
                case ConnectionKind.Npx:
                    return ("Connected (npx)" + (c.Profile != "standard" ? $" ({c.Profile})" : ""), _okStyle!,
                        $"Starts npx aker-mcp@latest, which follows new releases by itself; profile {c.Profile}." + projectNote);
                case ConnectionKind.DotnetTool:
                    return ("Connected (dotnet tool)" + (c.Profile != "standard" ? $" ({c.Profile})" : ""), _okStyle!,
                        $"Starts the .NET global tool aker-mcp (update it with: dotnet tool update -g AkerMcp.Server); profile {c.Profile}." + projectNote);
                case ConnectionKind.Broken:
                case ConnectionKind.Unreadable:
                    return ("Cannot start", _errorStyle!, c.Summary + projectNote);
                default:
                    return ("Your own setup", _mutedStyle!, c.Summary + $" (profile {c.Profile})." + projectNote);
            }
        }

        private RowAction ActionFor(ClientRow row)
        {
            var c = row.Connection;
            switch (c.Kind)
            {
                case ConnectionKind.NotConfigured: return RowAction.Connect;
                case ConnectionKind.Unreadable: return RowAction.None;
                case ConnectionKind.Broken: return RowAction.Repair;
                case ConnectionKind.Installed:
                    if (!c.RunsVersion(PluginVersion)) return RowAction.Update;
                    return c.Profile != SelectedProfile ? RowAction.Apply : RowAction.Disconnect;
                case ConnectionKind.Npx:
                case ConnectionKind.DotnetTool:
                    return c.Profile != SelectedProfile ? RowAction.Apply : RowAction.Disconnect;
                default: return RowAction.Replace;
            }
        }

        private (string label, string tooltip) Label(RowAction action)
        {
            switch (action)
            {
                case RowAction.Connect: return ("Connect", $"Install the AkerMCP server {PluginVersion} if needed and add it to this client. The file is backed up first.");
                case RowAction.Update: return ("Update", $"Point this client at the AkerMCP server {PluginVersion}, the version of this plugin.");
                case RowAction.Apply: return ("Apply", $"Switch this client to the '{SelectedProfile}' tool profile chosen under Advanced.");
                case RowAction.Disconnect: return ("Disconnect", "Remove the 'game-engine' entry from this client. Its other servers are left alone.");
                case RowAction.Repair: return ("Repair", $"Point this client at the AkerMCP server {PluginVersion}, which this plugin installs.");
                case RowAction.Replace: return ("Replace", $"Replace your own entry with the AkerMCP server {PluginVersion} that this plugin manages. The file is backed up first.");
                default: return ("", "");
            }
        }

        private void Run(ClientRow row, RowAction action)
        {
            if (action == RowAction.Disconnect)
            {
                var removed = McpConfigPatcher.Remove(row.Client);
                SetBanner(removed.Message, removed.Success ? MessageType.Info : MessageType.Error);
                RescanClients();
                return;
            }

            // A new profile for an npx or global-tool entry keeps the launcher it has: Apply is not a way to switch to the
            // installed server (Replace would be, and it is not offered for these).
            if (action == RowAction.Apply && row.Connection.Kind == ConnectionKind.Npx)
            {
                ConfigureWith(row.Client, McpServerLaunchSpec.NpxLatest(SelectedProfile));
                return;
            }
            if (action == RowAction.Apply && row.Connection.Kind == ConnectionKind.DotnetTool)
            {
                ConfigureWith(row.Client, McpServerLaunchSpec.DotnetTool(SelectedProfile));
                return;
            }

            if (ServerInstall.RuntimeId == null)
            {
                SetBanner("There is no prebuilt AkerMCP server for this computer: build it from source (see the Setup Guide under Advanced).", MessageType.Error);
                return;
            }

            if (ServerInstall.IsInstalled(PluginVersion))
            {
                ConfigureWithInstalledServer(row.Client);
                return;
            }

            if (!_waitingForServer.Contains(row.Client)) _waitingForServer.Add(row.Client);
            if (_downloading) return;

            _downloading = true;
            _downloadProgress = 0;
            _shownProgressPercent = -1;
            SetBanner(null, MessageType.Info);

            // Reports can arrive out of order: keep the furthest one.
            var progress = new Progress<double>(p => { if (p > _downloadProgress) _downloadProgress = (float)p; });
            ServerInstall.EnsureAsync(PluginVersion, UnityHttp.DownloadFile, progress).ContinueWith(t =>
            {
                var error = t.Exception?.GetBaseException();
                EditorMainThread.Post(() => OnServerReady(error));
            });
        }

        private void OnServerReady(Exception? error)
        {
            _downloading = false;
            var clients = _waitingForServer.ToList();
            _waitingForServer.Clear();

            if (error != null)
            {
                SetBanner($"Could not download the AkerMCP server {PluginVersion}: {error.Message}\n" +
                          $"Check the connection and try again, or download AkerMcp.Server-{ServerInstall.RuntimeId}.zip from the " +
                          "Releases page and run 'AkerMcp.Server setup' from the unpacked folder.", MessageType.Error);
                Repaint();
                return;
            }

            foreach (var client in clients)
                ConfigureWithInstalledServer(client);
        }

        private void ConfigureWithInstalledServer(AiClientDefinition client) =>
            ConfigureWith(client, McpServerLaunchSpec.Executable(ServerInstall.ExecutablePath(PluginVersion), SelectedProfile));

        private void ConfigureWith(AiClientDefinition client, McpServerLaunchSpec spec)
        {
            var result = McpConfigPatcher.Configure(client, spec);
            SetBanner(result.Message, result.Success ? MessageType.Info : MessageType.Error);
            RescanClients();
        }

        private void RescanClients()
        {
            _configSignature = ConfigSignature();
            _rows = AiClientCatalog.All
                .Select(c =>
                {
                    var status = McpConfigPatcher.Inspect(c);
                    return new ClientRow { Client = c, Status = status, Connection = AiConnection.Classify(status) };
                })
                .ToList();
            Repaint();
        }

        /// <summary>Existence and timestamp of every client config: a file stat each, no parsing.</summary>
        private static string ConfigSignature() =>
            string.Join("|", AiClientCatalog.All.Select(c =>
            {
                try { return File.Exists(c.ConfigPath) ? File.GetLastWriteTimeUtc(c.ConfigPath).Ticks.ToString() : "-"; }
                catch (Exception) { return "?"; }
            }));

        // ---- Plugin update -------------------------------------------------------------------------------------

        private void DrawPluginUpdate()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Plugin {PluginVersion}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_checkingUpdate || _importingUpdate))
            {
                if (GUILayout.Button(new GUIContent(_checkingUpdate ? "Checking..." : "Check for updates",
                        "Ask GitHub for the latest AkerMCP release. Nothing is checked unless you click."),
                        EditorStyles.miniButton, GUILayout.Width(120)))
                    StartUpdateCheck();
            }
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_updateMessage)) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(_updateMessage, EditorStyles.wordWrappedMiniLabel);
            if (_updateTag != null)
            {
                EditorGUILayout.BeginHorizontal();
                if (_updateNotesUrl != null && GUILayout.Button("What's new", EditorStyles.miniButton))
                    Application.OpenURL(_updateNotesUrl);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_importingUpdate))
                {
                    if (IsInstalledAsPackage())
                        EditorGUILayout.LabelField("Update it from Window > Package Manager.", EditorStyles.miniLabel);
                    else if (GUILayout.Button(new GUIContent(_importingUpdate ? "Downloading..." : $"Update to {_updateTag}",
                                 "Download AkerMCP.unitypackage of that release and import it over this one."), GUILayout.Width(130)))
                        DownloadAndImportPlugin(_updateTag);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void StartUpdateCheck()
        {
            _checkingUpdate = true;
            _updateMessage = null;
            _updateTag = null;
            _updateNotesUrl = null;
            Repaint();

            UnityHttp.GetText(LatestReleaseApiUrl).ContinueWith(task =>
            {
                string message;
                string? tag = null, notes = null;
                try
                {
                    using (var doc = JsonDocument.Parse(task.GetAwaiter().GetResult()))
                    {
                        var latestTag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                        var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : ReleasesPageUrl;
                        if (Version.TryParse(latestTag.TrimStart('v', 'V'), out var latest) && Version.TryParse(PluginVersion, out var current))
                        {
                            if (latest > current)
                            {
                                message = $"AkerMCP {latestTag} is out (you have {PluginVersion}). After the update, click Update next to each AI client.";
                                tag = latestTag;
                                notes = url;
                            }
                            else message = $"You have the latest version ({PluginVersion}).";
                        }
                        else message = $"The latest release is '{latestTag}', which is not a version this window understands.";
                    }
                }
                catch (Exception ex)
                {
                    message = $"Could not reach GitHub: {ex.GetBaseException().Message}";
                }

                EditorMainThread.Post(() =>
                {
                    _checkingUpdate = false;
                    _updateMessage = message;
                    _updateTag = tag;
                    _updateNotesUrl = notes;
                    Repaint();
                });
            });
        }

        private void DownloadAndImportPlugin(string tag)
        {
            _importingUpdate = true;
            Repaint();
            var url = $"https://github.com/{ServerInstall.Repository}/releases/download/{tag}/AkerMCP.unitypackage";
            var path = Path.Combine(Path.GetTempPath(), $"AkerMCP-{tag}.unitypackage");

            UnityHttp.DownloadFile(url, path, null, CancellationToken.None).ContinueWith(t =>
            {
                var error = t.Exception?.GetBaseException();
                EditorMainThread.Post(() =>
                {
                    _importingUpdate = false;
                    if (error != null)
                    {
                        _updateMessage = $"Could not download {tag}: {error.Message}";
                        Repaint();
                        return;
                    }
                    // Interactive: Unity shows what the package replaces, and the user confirms. Unity 6.6 deprecates this
                    // for AssetPackage.Package.Import, which the older editors the plugin also runs in do not have.
#pragma warning disable CS0618
                    AssetDatabase.ImportPackage(path, true);
#pragma warning restore CS0618
                });
            });
        }

        private static bool IsInstalledAsPackage()
        {
            try { return UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(McpEditorWindow).Assembly) != null; }
            catch (Exception) { return false; }
        }

        // ---- Advanced ------------------------------------------------------------------------------------------

        private string SelectedProfile
        {
            get
            {
                var saved = EditorPrefs.GetString(ProfilePref, "standard");
                return ProfileNames.Contains(saved) ? saved : "standard";
            }
        }

        private void DrawAdvanced()
        {
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
            if (!_showAdvanced) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var autoStart = EditorPrefs.GetBool(AutoRestartPref, true);
            var newAutoStart = EditorGUILayout.ToggleLeft(
                new GUIContent("Start AkerMCP when Unity opens and after each recompile", "Off: click Start yourself."), autoStart);
            if (newAutoStart != autoStart) EditorPrefs.SetBool(AutoRestartPref, newAutoStart);

            var allowExecute = EditorPrefs.GetBool(AllowExecutePref, true);
            var newAllowExecute = EditorGUILayout.ToggleLeft(new GUIContent("Allow the AI to run C# in this editor (execute)",
                "Off: the execute tool answers FORBIDDEN. Remembered across recompiles and restarts."), allowExecute);
            if (newAllowExecute != allowExecute)
            {
                EditorPrefs.SetBool(AllowExecutePref, newAllowExecute);
                if (UnityMcpPlugin.IsRunning) UnityMcpPlugin.Instance.EnableExecuteTool = newAllowExecute;
            }

            var profileIndex = Math.Max(0, Array.IndexOf(ProfileNames, SelectedProfile));
            var newProfileIndex = EditorGUILayout.Popup(new GUIContent("Tool profile", "What Connect, Update and Apply write. More tools cost more tokens per request."),
                profileIndex, ProfileLabels);
            if (newProfileIndex != profileIndex) EditorPrefs.SetString(ProfilePref, ProfileNames[newProfileIndex]);

            EditorGUILayout.Space(4);
            if (UnityMcpPlugin.IsRunning && !string.IsNullOrEmpty(UnityMcpPlugin.Instance.CurrentPipeName))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Pipe", GUILayout.Width(60));
                EditorGUILayout.SelectableLabel(UnityMcpPlugin.Instance.CurrentPipeName, GUILayout.Height(18));
                EditorGUILayout.EndHorizontal();
            }
            GUILayout.Label($"IPC protocol {IpcConstants.ProtocolVersion}", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Server folder", GUILayout.Width(80));
            EditorGUILayout.SelectableLabel(ServerInstall.Root, EditorStyles.miniLabel, GUILayout.Height(16));
            if (Directory.Exists(ServerInstall.Root) && GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(45)))
                EditorUtility.RevealInFinder(ServerInstall.Root);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Setup guide")) Application.OpenURL(DocsUrl);
            if (GUILayout.Button("Releases")) Application.OpenURL(ReleasesPageUrl);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ---- Shared bits ---------------------------------------------------------------------------------------

        /// <summary>
        /// Once per editor session, after the plugin starts: say so in the Console when an AI client still starts a server
        /// older or newer than this plugin (typically right after the plugin was updated). Never changes a file.
        /// </summary>
        internal static void LogClientsOnOtherServerVersions()
        {
            if (SessionState.GetBool(OutdatedNoticeSessionKey, false)) return;
            SessionState.SetBool(OutdatedNoticeSessionKey, true);
            try
            {
                foreach (var client in AiClientCatalog.All)
                {
                    if (!client.IsInstalled) continue;
                    var connection = AiConnection.Classify(McpConfigPatcher.Inspect(client));
                    if (connection.Kind == ConnectionKind.Installed && !connection.RunsVersion(PluginVersion))
                        Debug.Log($"[AkerMcp] {client.DisplayName} still starts the AkerMCP server {connection.ServerVersion}; this plugin is {PluginVersion}. " +
                                  "Open Window > AkerMcp and click Update next to it.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AkerMcp] Could not read the AI client configs: {ex.Message}");
            }
        }

        private void SetBanner(string? message, MessageType type)
        {
            _banner = message;
            _bannerType = type;
            Repaint();
        }

        private void EnsureStyles()
        {
            if (_okStyle != null) return;
            var dark = EditorGUIUtility.isProSkin;
            _okStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = dark ? new Color(0.40f, 0.85f, 0.45f) : new Color(0.05f, 0.45f, 0.12f) } };
            _warnStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = dark ? new Color(0.95f, 0.78f, 0.30f) : new Color(0.60f, 0.40f, 0.0f) } };
            _errorStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = dark ? new Color(1.0f, 0.45f, 0.40f) : new Color(0.70f, 0.10f, 0.05f) } };
            _mutedStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = dark ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.35f, 0.35f, 0.35f) } };
        }
    }
}
