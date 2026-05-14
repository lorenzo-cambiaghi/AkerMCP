#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    public class UnityCapabilities : IEngineCapabilities
    {
        private readonly Dictionary<string, Type> _aliases = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        public string EngineName => "Unity";
        public string EngineVersion => Application.unityVersion;
        public bool SupportsHotReload => true;
        public bool SupportsCodeExecution => true;

        public UnityCapabilities()
        {
            RegisterBuiltinTypes();
        }

        public IEnumerable<string> GetRegisteredTypeNames() => _aliases.Keys;

        public Type? ResolveType(string typeName)
        {
            if (_aliases.TryGetValue(typeName, out var type))
                return type;

            // Try full name
            type = Type.GetType(typeName);
            if (type != null) return type;

            // Try UnityEngine namespace
            type = typeof(GameObject).Assembly.GetType($"UnityEngine.{typeName}");
            if (type != null) return type;

            return null;
        }

        public void RegisterTypeAlias(string alias, Type type)
        {
            _aliases[alias] = type;
        }

        public static Type? ResolveUnityType(string typeName)
        {
            var builtins = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                ["Camera"] = typeof(Camera),
                ["Camera3D"] = typeof(Camera),
                ["Light"] = typeof(Light),
                ["Rigidbody"] = typeof(Rigidbody),
                ["RigidBody"] = typeof(Rigidbody),
                ["Rigidbody2D"] = typeof(Rigidbody2D),
                ["BoxCollider"] = typeof(BoxCollider),
                ["SphereCollider"] = typeof(SphereCollider),
                ["CapsuleCollider"] = typeof(CapsuleCollider),
                ["MeshRenderer"] = typeof(MeshRenderer),
                ["MeshFilter"] = typeof(MeshFilter),
                ["AudioSource"] = typeof(AudioSource),
                ["AudioListener"] = typeof(AudioListener),
                ["Canvas"] = typeof(Canvas),
                ["SpriteRenderer"] = typeof(SpriteRenderer),
                ["Animator"] = typeof(Animator),
                ["LineRenderer"] = typeof(LineRenderer),
                ["ParticleSystem"] = typeof(ParticleSystem),
            };

            if (builtins.TryGetValue(typeName, out var type))
                return type;

            // Try to find by name in loaded assemblies
            type = typeof(GameObject).Assembly.GetType($"UnityEngine.{typeName}");
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;

            return null;
        }

        private void RegisterBuiltinTypes()
        {
            // Math types
            _aliases["Vector2"] = typeof(Vector2);
            _aliases["Vector3"] = typeof(Vector3);
            _aliases["Vector4"] = typeof(Vector4);
            _aliases["Quaternion"] = typeof(Quaternion);
            _aliases["Matrix4x4"] = typeof(Matrix4x4);
            _aliases["Color"] = typeof(Color);
            _aliases["Color32"] = typeof(Color32);
            _aliases["Rect"] = typeof(Rect);
            _aliases["Bounds"] = typeof(Bounds);

            // Core types
            _aliases["GameObject"] = typeof(GameObject);
            _aliases["Transform"] = typeof(Transform);
            _aliases["Component"] = typeof(Component);

            // Common components
            _aliases["Camera"] = typeof(Camera);
            _aliases["Light"] = typeof(Light);
            _aliases["Rigidbody"] = typeof(Rigidbody);
            _aliases["Rigidbody2D"] = typeof(Rigidbody2D);
            _aliases["BoxCollider"] = typeof(BoxCollider);
            _aliases["SphereCollider"] = typeof(SphereCollider);
            _aliases["MeshRenderer"] = typeof(MeshRenderer);
            _aliases["MeshFilter"] = typeof(MeshFilter);
            _aliases["AudioSource"] = typeof(AudioSource);
            _aliases["Canvas"] = typeof(Canvas);
            _aliases["Animator"] = typeof(Animator);
        }
    }
}
