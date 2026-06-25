using MessagePack;

namespace AkerMcp.Shared.Ipc
{
    [MessagePackObject]
    public class IpcRequest
    {
        [Key(0)]
        public int Id { get; set; }

        [Key(1)]
        public string Method { get; set; } = null!;

        [Key(2)]
        public byte[]? Payload { get; set; }

        // Optional raw binary blob carried INTO the engine (e.g. a PNG to import as a
        // sprite). Payload stays the JSON metadata; Binary is the bytes. Mirrors how
        // IpcResponse separates Payload from ContentType for outbound binary.
        [Key(3)]
        public byte[]? Binary { get; set; }
    }

    [MessagePackObject]
    public class IpcResponse
    {
        [Key(0)]
        public int Id { get; set; }

        [Key(1)]
        public bool Success { get; set; }

        [Key(2)]
        public byte[]? Payload { get; set; }

        [Key(3)]
        public string? Error { get; set; }

        [Key(4)]
        public string? ContentType { get; set; }

        [Key(5)]
        public string? ErrorCode { get; set; }

        public static IpcResponse Ok(int id, byte[]? payload = null) => new IpcResponse
        {
            Id = id,
            Success = true,
            Payload = payload
        };

        public static IpcResponse Fail(int id, string error) => new IpcResponse
        {
            Id = id,
            Success = false,
            Error = error
        };

        public static IpcResponse FailWithCode(int id, string code, string error) => new IpcResponse
        {
            Id = id,
            Success = false,
            ErrorCode = code,
            Error = error
        };

        public static IpcResponse Binary(int id, byte[] payload, string contentType) => new IpcResponse
        {
            Id = id,
            Success = true,
            Payload = payload,
            ContentType = contentType
        };
    }
}
