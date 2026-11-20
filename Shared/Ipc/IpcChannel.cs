using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace AkerMcp.Shared.Ipc
{
    /// <summary>A message the other side would refuse: it is over <see cref="IpcConstants.MaxFrameBytes"/>.</summary>
    public sealed class IpcFrameTooLargeException : InvalidOperationException
    {
        public IpcFrameTooLargeException(int length)
            : base($"An IPC message of {length / (1024.0 * 1024.0):0.#} MB is over the " +
                   $"{IpcConstants.MaxFrameBytes / (1024 * 1024)} MB limit.")
        {
            Length = length;
        }

        public int Length { get; }
    }

    public class IpcChannel : IDisposable
    {
        private readonly Stream _stream;
        // Never disposed: a SemaphoreSlim disposed under a waiter leaves the waiter hanging and turns its Release into
        // an ObjectDisposedException that hides the real error. Holding no unmanaged handle (AvailableWaitHandle is
        // never touched), it is left to the GC.
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _readLock = new SemaphoreSlim(1, 1);
        private int _disposed;
        private volatile bool _faulted;

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

        private void ThrowIfUnusable()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(IpcChannel));
            if (_faulted)
                throw new IOException("The IPC channel was interrupted in the middle of a message and can no longer be used.");
        }

        private async Task WriteFrame(byte[] data, CancellationToken ct)
        {
            // Refused here, whole, rather than by the reader: the reader drops the connection on an oversize frame, and
            // the caller then read "engine disconnected" for what was a message too big to send.
            if (data.Length > IpcConstants.MaxFrameBytes) throw new IpcFrameTooLargeException(data.Length);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            byte[]? frame = null;
            try
            {
                ThrowIfUnusable();

                // Length prefix and body in one buffer and one write: no window in which the header is out and the
                // body is not.
                frame = ArrayPool<byte>.Shared.Rent(4 + data.Length);
                frame[0] = (byte)data.Length;
                frame[1] = (byte)(data.Length >> 8);
                frame[2] = (byte)(data.Length >> 16);
                frame[3] = (byte)(data.Length >> 24);
                Buffer.BlockCopy(data, 0, frame, 4, data.Length);

                try
                {
                    await _stream.WriteAsync(frame, 0, 4 + data.Length, ct).ConfigureAwait(false);
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Cancelled or failed with part of the frame possibly on the wire: the next frame would be read
                    // from the middle of this one.
                    _faulted = true;
                    throw;
                }
            }
            finally
            {
                if (frame != null) ArrayPool<byte>.Shared.Return(frame);
                _writeLock.Release();
            }
        }

        private async Task<byte[]> ReadFrame(CancellationToken ct)
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfUnusable();
                try
                {
                    var lengthBytes = new byte[4];
                    await ReadExactly(lengthBytes, ct).ConfigureAwait(false);

                    var length = lengthBytes[0] | (lengthBytes[1] << 8) | (lengthBytes[2] << 16) | (lengthBytes[3] << 24);
                    if (length <= 0 || length > IpcConstants.MaxFrameBytes)
                        throw new InvalidOperationException($"Invalid frame length: {length}");

                    var data = new byte[length];
                    await ReadExactly(data, ct).ConfigureAwait(false);
                    return data;
                }
                catch
                {
                    // Same as a write: a read stopped half way leaves the stream in the middle of a frame.
                    _faulted = true;
                    throw;
                }
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stream.Dispose();
        }
    }
}
