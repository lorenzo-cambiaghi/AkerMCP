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

            // Any component of any loaded assembly: physics 2D, AI, UI, TextMeshPro, the project's own scripts.
            return ResolveComponentType(typeName, out _);
        }

        public void RegisterTypeAlias(string alias, Type type)
        {
            _aliases[alias] = type;
        }

        /// <summary>
        /// The component type <c>create</c> adds for <paramref name="typeName"/>: a built-in alias, a full name
        /// ("UnityEngine.UI.Image", "TMPro.TextMeshProUGUI") or a short name ("BoxCollider2D", "NavMeshAgent",
        /// "PlayerController"), matched case-insensitively across every loaded assembly. Null with the reason in
        /// <paramref name="problem"/> when nothing — or more than one thing — matches.
        /// <para>
        /// It used to look in UnityEngine.CoreModule only. Every component that lives elsewhere (Physics2D, AI, UI,
        /// TextMeshPro, user MonoBehaviours) resolved to nothing, and <c>create</c> made an empty GameObject and
        /// reported success.
        /// </para>
        /// </summary>
        public static Type? ResolveComponentType(string typeName, out string? problem)
        {
            problem = null;
            var name = (typeName ?? "").Trim();

            if (Builtins.TryGetValue(name, out var builtin))
                return builtin;

            var index = ComponentIndex.Instance;
            if (index.ByFullName.TryGetValue(name, out var exact))
                return exact;

            if (index.ByName.TryGetValue(name, out var candidates))
            {
                if (candidates.Count == 1) return candidates[0];

                // The engine's own type wins a short-name clash with a package or project type of the same name.
                var engine = candidates.Where(t => t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)).ToList();
                if (engine.Count == 1) return engine[0];

                problem = $"Component type '{name}' is ambiguous: {string.Join(", ", candidates.Select(t => t.FullName).OrderBy(n => n))}. " +
                          "Pass the full name.";
                return null;
            }

            problem = $"Unknown component type '{name}': no loaded assembly defines an addable Component with that name. " +
                      "Use type \"GameObject\" for an empty object, a primitive (Cube, Sphere, Capsule, Cylinder, Plane, Quad), " +
                      "or a component's name or full name (e.g. \"BoxCollider2D\", \"UnityEngine.UI.Image\").";
            return null;
        }

        /// <summary>Kept for existing callers: the component type, or null.</summary>
        public static Type? ResolveUnityType(string typeName) => ResolveComponentType(typeName, out _);

        // Names that ask for a GameObject with nothing on it.
        public static bool IsEmptyObjectType(string typeName)
        {
            switch ((typeName ?? "").Trim().ToLowerInvariant())
            {
                case "gameobject":
                case "empty":
                case "transform":
                    return true;
                default:
                    return false;
            }
        }

        // "Cube" etc. are not components: they are GameObject.CreatePrimitive shapes. They used to fall through to an
        // empty GameObject that happened to be named "Cube".
        public static bool TryGetPrimitive(string typeName, out PrimitiveType primitive)
        {
            switch ((typeName ?? "").Trim().ToLowerInvariant())
            {
                case "cube": primitive = PrimitiveType.Cube; return true;
                case "sphere": primitive = PrimitiveType.Sphere; return true;
                case "capsule": primitive = PrimitiveType.Capsule; return true;
                case "cylinder": primitive = PrimitiveType.Cylinder; return true;
                case "plane": primitive = PrimitiveType.Plane; return true;
                case "quad": primitive = PrimitiveType.Quad; return true;
                default: primitive = default; return false;
            }
        }

        /// <summary>
        /// Every addable component type of the loaded assemblies, by short and by full name. Built once per domain:
        /// the set of compiled types only changes with a domain reload, which drops this cache along with the rest.
        /// </summary>
        private sealed class ComponentIndex
        {
            private static ComponentIndex? _instance;
            public static ComponentIndex Instance => _instance ??= new ComponentIndex();

            public readonly Dictionary<string, List<Type>> ByName = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Type> ByFullName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            private ComponentIndex()
            {
                foreach (var type in AllComponentTypes())
                {
                    // Not addable: abstract bases, open generics, and the Transform every GameObject already has.
                    if (type.IsAbstract || type.IsGenericTypeDefinition || typeof(Transform).IsAssignableFrom(type))
                        continue;
                    if (type.FullName == null) continue;

                    ByFullName[type.FullName] = type;
                    if (!ByName.TryGetValue(type.Name, out var list))
                        ByName[type.Name] = list = new List<Type>();
                    list.Add(type);
                }
            }

            private static IEnumerable<Type> AllComponentTypes()
            {
#if UNITY_EDITOR
                // The editor's prebuilt index of derived types: no walk over every assembly.
                return UnityEditor.TypeCache.GetTypesDerivedFrom<Component>();
#else
                var found = new List<Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                    catch { continue; }
                    foreach (var t in types)
                        if (typeof(Component).IsAssignableFrom(t)) found.Add(t);
                }
                return found;
#endif
            }
        }

        private static readonly Dictionary<string, Type> Builtins = CreateBuiltins();

        private static Dictionary<string, Type> CreateBuiltins()
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
            return builtins;
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
