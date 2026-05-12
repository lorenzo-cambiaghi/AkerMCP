using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCPSharp.Shared.Protocol;

namespace MCPSharp.Server
{
    public class StdioTransport : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly JsonSerializerOptions _jsonOptions;

        public StdioTransport()
        {
            _reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            _writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        public async Task<JsonElement?> ReadMessage(CancellationToken ct)
        {
            var line = await _reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) return await ReadMessage(ct).ConfigureAwait(false);

            try
            {
                return JsonSerializer.Deserialize<JsonElement>(line);
            }
            catch (JsonException ex)
            {
                LogError($"Failed to parse JSON-RPC message: {ex.Message}");
                return null;
            }
        }

        public async Task SendResponse(JsonRpcResponse response, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(response, _jsonOptions);
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
        }

        public async Task SendNotification(JsonRpcNotification notification, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(notification, _jsonOptions);
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
        }

        public static void LogError(string message)
        {
            Console.Error.WriteLine($"[MCPSharp] ERROR: {message}");
        }

        public static void LogInfo(string message)
        {
            Console.Error.WriteLine($"[MCPSharp] INFO: {message}");
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
        }
    }
}
