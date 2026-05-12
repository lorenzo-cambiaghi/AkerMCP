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
    }
}
