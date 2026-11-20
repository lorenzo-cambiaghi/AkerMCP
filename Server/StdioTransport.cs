using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    /// <summary>What one line of stdin turned out to be.</summary>
    public readonly struct IncomingMessage
    {
        private IncomingMessage(bool ended, JsonElement message, string? parseError)
        {
            Ended = ended;
            Message = message;
            ParseError = parseError;
        }

        /// <summary>The client closed stdin (or the server is shutting down): nothing more will arrive.</summary>
        public bool Ended { get; }

        /// <summary>A line that is not JSON. Answered with -32700, and the server keeps reading.</summary>
        public string? ParseError { get; }

        public JsonElement Message { get; }

        public static IncomingMessage End => new IncomingMessage(true, default, null);
        public static IncomingMessage Of(JsonElement message) => new IncomingMessage(false, message, null);
        public static IncomingMessage Malformed(string error) => new IncomingMessage(false, default, error);
    }

    public class StdioTransport : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly JsonSerializerOptions _jsonOptions;

        // Requests are answered from more than one task (a ping while a tool call runs), and two
        // WriteLineAsync calls on one StreamWriter interleave their characters.
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

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

        /// <summary>
        /// The next message. A malformed line is not the end of the session: it used to come back
        /// as null, the same value as end of input, and one stray line closed the server.
        /// </summary>
        public async Task<IncomingMessage> ReadMessage(CancellationToken ct)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return IncomingMessage.End;
                }

                if (line == null) return IncomingMessage.End;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    return IncomingMessage.Of(JsonSerializer.Deserialize<JsonElement>(line));
                }
                catch (JsonException ex)
                {
                    LogError($"Failed to parse JSON-RPC message: {ex.Message}");
                    return IncomingMessage.Malformed(ex.Message);
                }
            }
        }

        public Task SendResponse(JsonRpcResponse response, CancellationToken ct) => Send(response, ct);

        /// <summary>The answers to a JSON-RPC batch, written as one array.</summary>
        public Task SendBatch(JsonRpcResponse[] responses, CancellationToken ct) => Send(responses, ct);

        public Task SendNotification(JsonRpcNotification notification, CancellationToken ct) => Send(notification, ct);

        private async Task Send<T>(T message, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(message, _jsonOptions);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(json).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public static void LogError(string message)
        {
            Console.Error.WriteLine($"[AkerMcp] ERROR: {message}");
        }

        public static void LogInfo(string message)
        {
            Console.Error.WriteLine($"[AkerMcp] INFO: {message}");
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
        }
    }
}
