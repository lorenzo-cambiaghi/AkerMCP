using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace MCPSharp.Shared.Ipc
{
    public class IpcChannel : IDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _readLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public IpcChannel(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public async Task SendRequest(IpcRequest request, CancellationToken ct = default)
        {
            var payload = MessagePackSerializer.Serialize(request);
            await WriteFrame(payload, ct).ConfigureAwait(false);
        }

        public async Task SendResponse(IpcResponse response, CancellationToken ct = default)
        {
            var payload = MessagePackSerializer.Serialize(response);
            await WriteFrame(payload, ct).ConfigureAwait(false);
        }

        public async Task<IpcRequest> ReceiveRequest(CancellationToken ct = default)
        {
            var payload = await ReadFrame(ct).ConfigureAwait(false);
            return MessagePackSerializer.Deserialize<IpcRequest>(payload);
        }

        public async Task<IpcResponse> ReceiveResponse(CancellationToken ct = default)
        {
            var payload = await ReadFrame(ct).ConfigureAwait(false);
            return MessagePackSerializer.Deserialize<IpcResponse>(payload);
        }

        private async Task WriteFrame(byte[] data, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lengthBytes = BitConverter.GetBytes(data.Length);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(lengthBytes);

                await _stream.WriteAsync(lengthBytes, 0, 4, ct).ConfigureAwait(false);
                await _stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<byte[]> ReadFrame(CancellationToken ct)
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lengthBytes = new byte[4];
                await ReadExactly(lengthBytes, ct).ConfigureAwait(false);

                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(lengthBytes);

                var length = BitConverter.ToInt32(lengthBytes, 0);
                if (length <= 0 || length > 64 * 1024 * 1024)
                    throw new InvalidOperationException($"Invalid frame length: {length}");

                var data = new byte[length];
                await ReadExactly(data, ct).ConfigureAwait(false);
                return data;
            }
            finally
            {
                _readLock.Release();
            }
        }

        private async Task ReadExactly(byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _stream.ReadAsync(buffer, offset, buffer.Length - offset, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Connection closed while reading frame");
                offset += read;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeLock.Dispose();
            _readLock.Dispose();
            _stream.Dispose();
        }
    }
}
