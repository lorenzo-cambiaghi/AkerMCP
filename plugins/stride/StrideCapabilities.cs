#nullable enable
using System;
using System.Collections.Generic;
using AkerMcp.Shared.Abstraction;
using Stride.Engine;

namespace AkerMcp.StrideAdapter
{
    public sealed class StrideCapabilities : IEngineCapabilities
    {
        private readonly Dictionary<string, Type> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public string EngineName => "Stride";

        public string EngineVersion
            => typeof(Entity).Assembly.GetName().Version?.ToString() ?? "unknown";

        // Milestone 1: read-only walking skeleton. Flipped on as those land.
        public bool SupportsHotReload => false;
        public bool SupportsCodeExecution => false;

        public StrideCapabilities() => RegisterBuiltinTypes();

        public IEnumerable<string> GetRegisteredTypeNames() => _aliases.Keys;

        public Type? ResolveType(string typeName)
        {
            if (_aliases.TryGetValue(typeName, out var type)) return type;

            type = Type.GetType(typeName);
            if (type != null) return type;

            // Engine types live in the Stride.Engine assembly (Entity, components…).
            var engineAsm = typeof(Entity).Assembly;
            return engineAsm.GetType(typeName)
                ?? engineAsm.GetType($"Stride.Engine.{typeName}");
        }

        public void RegisterTypeAlias(string alias, Type type) => _aliases[alias] = type;

        private void RegisterBuiltinTypes()
        {
            _aliases["Entity"] = typeof(Entity);
            _aliases["TransformComponent"] = typeof(TransformComponent);
            _aliases["ModelComponent"] = typeof(ModelComponent);
            _aliases["CameraComponent"] = typeof(CameraComponent);
            _aliases["LightComponent"] = typeof(LightComponent);
            _aliases["ScriptComponent"] = typeof(ScriptComponent);
        }
    }
}
