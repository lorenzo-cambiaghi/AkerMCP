using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AkerMcp.UiProbe
{
    /// <summary>
    /// A minimal MCP client over stdio: start the server, speak JSON lines, read the answers.
    ///
    /// It exists so the bench can talk to the server the model talks to, without going through
    /// an editor session. That matters more than it sounds: the build a client is configured
    /// to launch is often not the build just compiled, and every measurement here is worthless
    /// if it was taken against the wrong binary. The path is therefore explicit and printed.
    /// </summary>
    public sealed class McpStdio : IDisposable
    {
        private readonly Process _server;
        private int _id;

        private McpStdio(Process server) => _server = server;

        public static async Task<McpStdio> Start(string serverPath, string? profile, IDictionary<string, string>? env)
        {
            if (!File.Exists(serverPath))
                throw new FileNotFoundException(
                    $"No server at '{serverPath}'. Build it first (dotnet build Server/AkerMcp.Server.csproj) " +
                    "or pass --server <path>.", serverPath);

            var psi = new ProcessStartInfo
            {
                FileName = serverPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrWhiteSpace(profile))
            {
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(profile!);
            }
            if (env != null)
                foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            var proc = Process.Start(psi)
                       ?? throw new InvalidOperationException("Could not start the server process.");
            var client = new McpStdio(proc);

            await client.Request("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "aker-ui-probe", version = "1.0" },
            });
            client.Notify("notifications/initialized");
            return client;
        }

        public async Task<JsonElement> Request(string method, object? parameters)
        {
            int id = ++_id;
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };
            if (parameters != null) payload["params"] = parameters;

            await _server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload));
            await _server.StandardInput.FlushAsync();

            while (true)
            {
                var line = await _server.StandardOutput.ReadLineAsync();
                if (line == null) throw new IOException("The server closed its output.");
                if (line.Trim().Length == 0) continue;

                JsonElement message;
                try { message = JsonSerializer.Deserialize<JsonElement>(line); }
                catch (JsonException) { continue; } // a log line that reached stdout
                if (message.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number
                    && mid.GetInt32() == id)
                    return message;
            }
        }

        public void Notify(string method)
        {
            _server.StandardInput.WriteLine(
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = method,
                }));
            _server.StandardInput.Flush();
        }

        /// <summary>Call a tool and return its result, with the text and images pulled apart.</summary>
        public async Task<ToolAnswer> Call(string tool, object arguments)
        {
            var response = await Request("tools/call", new { name = tool, arguments });
            if (response.TryGetProperty("error", out var err))
                return new ToolAnswer(true, err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "error",
                                      Array.Empty<byte[]>());

            var result = response.GetProperty("result");
            bool isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;

            var text = new StringBuilder();
            var images = new List<byte[]>();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text" && item.TryGetProperty("text", out var tx))
                    {
                        if (text.Length > 0) text.Append(' ');
                        text.Append(tx.GetString());
                    }
                    else if (type == "image" && item.TryGetProperty("data", out var d))
                    {
                        try { images.Add(Convert.FromBase64String(d.GetString() ?? "")); }
                        catch (FormatException) { /* keep the text, drop the image */ }
                    }
                }
            }
            return new ToolAnswer(isError, text.ToString(), images.ToArray());
        }

        /// <summary>Whatever the server logged to stderr, for the handshake lines worth seeing.</summary>
        public string DrainLog()
        {
            try
            {
                _server.StandardInput.Close();
                if (!_server.WaitForExit(1500)) _server.Kill();
                return _server.StandardError.ReadToEnd();
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            try { if (!_server.HasExited) _server.Kill(); } catch { /* already gone */ }
            _server.Dispose();
        }
    }

    public sealed record ToolAnswer(bool IsError, string Text, byte[][] Images);
}
