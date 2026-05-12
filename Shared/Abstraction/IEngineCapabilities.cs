using System;
using System.Collections.Generic;

namespace AkerMcp.Shared.Abstraction
{
    public interface IEngineCapabilities
    {
        string EngineName { get; }
        string EngineVersion { get; }
        bool SupportsHotReload { get; }
        bool SupportsCodeExecution { get; }
        IEnumerable<string> GetRegisteredTypeNames();
        Type? ResolveType(string typeName);
        void RegisterTypeAlias(string alias, Type type);
    }
}
