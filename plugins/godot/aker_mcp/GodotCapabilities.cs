#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    public class GodotCapabilities : IEngineCapabilities
    {
        private readonly Dictionary<string, Type> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public string EngineName => "Godot";

        public string EngineVersion
        {
            get
            {
                var info = Engine.GetVersionInfo();
                return info.TryGetValue("string", out var s) ? s.AsString() : "unknown";
            }
        }

        public bool SupportsHotReload => true;
        public bool SupportsCodeExecution => true;

        public GodotCapabilities() => RegisterBuiltinTypes();

        public IEnumerable<string> GetRegisteredTypeNames() => _aliases.Keys;

        public Type? ResolveType(string typeName)
        {
            if (_aliases.TryGetValue(typeName, out var type)) return type;

            type = Type.GetType(typeName);
            if (type != null) return type;

            // Anything in the GodotSharp assembly (Node3D, Camera3D, Mesh, ...).
            return typeof(Node).Assembly.GetType($"Godot.{typeName}");
        }

        public void RegisterTypeAlias(string alias, Type type) => _aliases[alias] = type;

        /// <summary>Resolve a Godot engine/script type by simple name (for node creation).</summary>
        public static Type? ResolveGodotType(string typeName)
            => typeof(Node).Assembly.GetType($"Godot.{typeName}");

        private void RegisterBuiltinTypes()
        {
            // Math / value types
            _aliases["Vector2"] = typeof(Vector2);
            _aliases["Vector2I"] = typeof(Vector2I);
            _aliases["Vector3"] = typeof(Vector3);
            _aliases["Vector3I"] = typeof(Vector3I);
            _aliases["Vector4"] = typeof(Vector4);
            _aliases["Vector4I"] = typeof(Vector4I);
            _aliases["Quaternion"] = typeof(Quaternion);
            _aliases["Basis"] = typeof(Basis);
            _aliases["Transform2D"] = typeof(Transform2D);
            _aliases["Transform3D"] = typeof(Transform3D);
            _aliases["Color"] = typeof(Color);
            _aliases["Rect2"] = typeof(Rect2);
            _aliases["Rect2I"] = typeof(Rect2I);
            _aliases["Aabb"] = typeof(Aabb);
            _aliases["Plane"] = typeof(Plane);

            // Core nodes
            _aliases["Node"] = typeof(Node);
            _aliases["Node2D"] = typeof(Node2D);
            _aliases["Node3D"] = typeof(Node3D);
            _aliases["Control"] = typeof(Control);

            // Common 3D nodes
            _aliases["Camera3D"] = typeof(Camera3D);
            _aliases["MeshInstance3D"] = typeof(MeshInstance3D);
            _aliases["DirectionalLight3D"] = typeof(DirectionalLight3D);
            _aliases["OmniLight3D"] = typeof(OmniLight3D);
            _aliases["SpotLight3D"] = typeof(SpotLight3D);
            _aliases["RigidBody3D"] = typeof(RigidBody3D);
            _aliases["StaticBody3D"] = typeof(StaticBody3D);
            _aliases["CharacterBody3D"] = typeof(CharacterBody3D);
            _aliases["CollisionShape3D"] = typeof(CollisionShape3D);
            _aliases["Area3D"] = typeof(Area3D);

            // Common 2D nodes
            _aliases["Camera2D"] = typeof(Camera2D);
            _aliases["Sprite2D"] = typeof(Sprite2D);
            _aliases["RigidBody2D"] = typeof(RigidBody2D);
            _aliases["StaticBody2D"] = typeof(StaticBody2D);
            _aliases["CharacterBody2D"] = typeof(CharacterBody2D);
            _aliases["CollisionShape2D"] = typeof(CollisionShape2D);
            _aliases["Area2D"] = typeof(Area2D);

            // Common UI
            _aliases["Label"] = typeof(Label);
            _aliases["Button"] = typeof(Button);
            _aliases["Panel"] = typeof(Panel);

            // Audio
            _aliases["AudioStreamPlayer"] = typeof(AudioStreamPlayer);
            _aliases["AudioStreamPlayer3D"] = typeof(AudioStreamPlayer3D);
        }
    }
}
