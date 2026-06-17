#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Reflection;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Wraps a Godot <see cref="Node"/>. Godot has no Unity-style component model:
    /// the node itself is the object, so property/method resolution targets it
    /// directly via reflection (case-insensitive, so "position.x" == "Position.X").
    /// </summary>
    public class GodotSceneNode : ISceneNode
    {
        private readonly Node _node;
        private readonly PropertyPathResolver _resolver = new();

        public GodotSceneNode(Node node) => _node = node;

        public string Name => _node.Name.ToString();

        public string Path
        {
            get
            {
                var root = EditorInterface.Singleton.GetEditedSceneRoot();
                var names = new List<string>();
                Node? n = _node;
                while (n != null)
                {
                    names.Insert(0, n.Name.ToString());
                    if (n == root) break;
                    n = n.GetParent();
                }
                return "/" + string.Join("/", names);
            }
        }

        public string TypeName => _node.GetType().Name;

        public ISceneNode? Parent
        {
            get
            {
                var parent = _node.GetParent();
                return parent != null ? new GodotSceneNode(parent) : null;
            }
        }

        public IEnumerable<ISceneNode> Children
        {
            get
            {
                foreach (Node child in _node.GetChildren())
                    yield return new GodotSceneNode(child);
            }
        }

        public object UnderlyingObject => _node;

        public object? GetProperty(string propertyPath)
        {
            if (propertyPath.Equals("name", StringComparison.OrdinalIgnoreCase))
                return _node.Name.ToString();

            return _resolver.Resolve(_node, propertyPath);
        }

        public void SetProperty(string propertyPath, object? value)
        {
            if (propertyPath.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (value != null) _node.Name = value.ToString() ?? string.Empty;
                return;
            }

            _resolver.Set(_node, propertyPath, value);
        }

        public object? CallMethod(string methodName, object?[]? args)
        {
            var method = _node.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (method != null)
                return new AkerMcp.Shared.Reflection.MethodInvoker().Invoke(_node, methodName, args);

            throw new PropertyPathException(
                $"Method '{methodName}' not found on '{Name}' ({TypeName})");
        }

        public IEnumerable<ComponentInfo> GetComponents()
        {
            // Godot nodes aren't composed of components; surface the node's own
            // concrete type so the hierarchy renders as "Player  [Node3D]".
            var type = _node.GetType();
            yield return new ComponentInfo
            {
                Name = type.Name,
                FullTypeName = type.FullName ?? type.Name,
                Enabled = true
            };
        }

        public IEnumerable<PropertyDescriptor> GetProperties()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            yield return new PropertyDescriptor
            {
                Name = "name", TypeName = "string", CanRead = true, CanWrite = true,
                Value = _node.Name.ToString()
            };
            seen.Add("name");

            foreach (var prop in _node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!seen.Add(prop.Name)) continue;

                object? value = null;
                try { if (prop.CanRead) value = prop.GetValue(_node); } catch { }

                yield return new PropertyDescriptor
                {
                    Name = prop.Name,
                    TypeName = prop.PropertyType.Name,
                    CanRead = prop.CanRead,
                    CanWrite = prop.CanWrite,
                    Value = IsSimple(prop.PropertyType) ? value : value?.ToString()
                };
            }
        }

        public IEnumerable<MethodDescriptor> GetMethods()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skip = new HashSet<string>
            {
                "Equals", "GetHashCode", "GetType", "ToString",
                "Get", "Set", "Call", "CallDeferred", "Connect", "Disconnect",
                "GetClass", "IsClass", "GetInstanceId", "EmitSignal",
                "GetIndexed", "SetIndexed", "ToSignal", "HasMethod", "HasSignal"
            };

            foreach (var method in _node.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.IsSpecialName) continue;
                if (skip.Contains(method.Name)) continue;
                if (!seen.Add(method.Name)) continue;

                yield return new MethodDescriptor
                {
                    Name = method.Name,
                    ReturnType = method.ReturnType.Name,
                    Parameters = method.GetParameters().Select(p => new ParameterDescriptor
                    {
                        Name = p.Name ?? "arg",
                        TypeName = p.ParameterType.Name,
                        IsOptional = p.IsOptional
                    }).ToList()
                };
            }
        }

        private static bool IsSimple(Type type)
        {
            return type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum
                || type == typeof(Vector2) || type == typeof(Vector2I)
                || type == typeof(Vector3) || type == typeof(Vector3I)
                || type == typeof(Vector4) || type == typeof(Vector4I)
                || type == typeof(Quaternion) || type == typeof(Color)
                || type == typeof(Rect2) || type == typeof(Rect2I) || type == typeof(Aabb)
                || type == typeof(Plane);
        }
    }
}
