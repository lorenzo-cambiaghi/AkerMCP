using System.Reflection;

namespace AkerMcp.Server
{
    /// <summary>
    /// The product version, the one on the GitHub release and in the MCP handshake.
    ///
    /// Two versions live in this repository and they are not the same number. The
    /// IPC protocol version (<see cref="AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion"/>)
    /// says which contract the server and an engine plugin speak, and it is what the
    /// AkerMcp.Shared and AkerMcp.Client packages are versioned by. This one says
    /// which release of the product you are running. They drift on purpose: v1.4.0
    /// shipped protocol 1.5.0.
    ///
    /// It is read from the assembly, so `Version` in AkerMcp.Server.csproj is the
    /// single place to change it. Before this existed the handshake announced a
    /// hard-coded "1.0.0" through six releases, and the startup log announced the
    /// protocol version as if it were the server's.
    /// </summary>
    public static class ServerVersion
    {
        public static string Product { get; } = Read();

        private static string Read()
        {
            var assembly = typeof(ServerVersion).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
            // The SDK appends "+<commit>" when SourceLink is on.
            var plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }
    }
}
