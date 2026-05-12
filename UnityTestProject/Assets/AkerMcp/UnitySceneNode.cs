#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Reflection;

namespace AkerMcp.Unity
{
    public class UnitySceneNode : ISceneNode
    {
        private readonly GameObject _go;
        private readonly PropertyPathResolver _resolver;

        public UnitySceneNode(GameObject go)
        {
            _go = go;
            _resolver = new PropertyPathResolver();
        }

        public string Name => _go.name;

        public string Path
        {
            get
            {
                var path = _go.name;
                var t = _go.transform.parent;
                while (t != null)
                {
                    path = t.name + "/" + path;
                    t = t.parent;
                }
                return "/" + path;
            }
        }

        public string TypeName
        {
            get
            {
                var components = _go.GetComponents<Component>();
                for (int i = components.Length - 1; i >= 0; i--)
                {
                    if (components[i] != null && !(components[i] is Transform))
                        return components[i].GetType().Name;
                }
                return "GameObject";
            }
        }

        public ISceneNode? Parent
        {
            get
            {
                var parentTransform = _go.transform.parent;
                return parentTransform != null ? new UnitySceneNode(parentTransform.gameObject) : null;
            }
        }

        public IEnumerable<ISceneNode> Children
        {
            get
            {
                for (int i = 0; i < _go.transform.childCount; i++)
                    yield return new UnitySceneNode(_go.transform.GetChild(i).gameObject);
            }
        }

        public object UnderlyingObject => _go;

        public object? GetProperty(string propertyPath)
        {
            var segments = propertyPath.Split(new[] { '.' }, 2);
            var firstSegment = segments[0];
            var remainder = segments.Length > 1 ? segments[1] : null;

            // Direct GameObject properties
            if (TryGetGameObjectProperty(firstSegment, remainder, out var goResult))
                return goResult;

            // Try Transform first (most common)
            if (TryResolveOnComponent(_go.transform, propertyPath))
                return _resolver.Resolve(_go.transform, propertyPath);

            // Try each component
            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null || component is Transform) continue;
                if (TryResolveOnComponent(component, propertyPath))
                    return _resolver.Resolve(component, propertyPath);
            }

            throw new PropertyPathException(
                $"Property '{propertyPath}' not found on '{Name}' or any of its components");
        }

        public void SetProperty(string propertyPath, object? value)
        {
            // Direct GameObject properties
            if (TrySetGameObjectProperty(propertyPath, value))
                return;

            // Try Transform first
            if (TryResolveOnComponent(_go.transform, propertyPath))
            {
                _resolver.Set(_go.transform, propertyPath, value);
                return;
            }

            // Try each component
            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null || component is Transform) continue;
                if (TryResolveOnComponent(component, propertyPath))
                {
                    _resolver.Set(component, propertyPath, value);
                    return;
                }
            }

            throw new PropertyPathException(
                $"Cannot set property '{propertyPath}' on '{Name}' — not found on any component");
        }

        public object? CallMethod(string methodName, object?[]? args)
        {
            var invoker = new MethodInvoker();

            // Try each component
            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null) continue;
                var method = component.GetType().GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (method != null)
                    return invoker.Invoke(component, methodName, args);
            }

            // Try on GameObject itself
            var goMethod = _go.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (goMethod != null)
                return invoker.Invoke(_go, methodName, args);

            throw new PropertyPathException(
                $"Method '{methodName}' not found on '{Name}' or any of its components");
        }

        public IEnumerable<ComponentInfo> GetComponents()
        {
            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();
                bool enabled = true;
                if (component is Behaviour b) enabled = b.enabled;
                else if (component is Renderer r) enabled = r.enabled;
                else if (component is Collider c) enabled = c.enabled;

                yield return new ComponentInfo
                {
                    Name = type.Name,
                    FullTypeName = type.FullName ?? type.Name,
                    Enabled = enabled
                };
            }
        }

        public IEnumerable<PropertyDescriptor> GetProperties()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // GameObject basics
            yield return new PropertyDescriptor { Name = "name", TypeName = "string", CanRead = true, CanWrite = true, Value = _go.name };
            yield return new PropertyDescriptor { Name = "tag", TypeName = "string", CanRead = true, CanWrite = true, Value = _go.tag };
            yield return new PropertyDescriptor { Name = "layer", TypeName = "int", CanRead = true, CanWrite = true, Value = _go.layer };
            yield return new PropertyDescriptor { Name = "activeSelf", TypeName = "bool", CanRead = true, CanWrite = false, Value = _go.activeSelf };
            seen.Add("name"); seen.Add("tag"); seen.Add("layer"); seen.Add("activeSelf");

            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();
                var prefix = component is Transform ? "" : type.Name + ".";

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    var displayName = prefix + prop.Name;
                    if (!seen.Add(displayName)) continue;

                    object? value = null;
                    try { if (prop.CanRead) value = prop.GetValue(component); } catch { }

                    yield return new PropertyDescriptor
                    {
                        Name = displayName,
                        TypeName = prop.PropertyType.Name,
                        CanRead = prop.CanRead,
                        CanWrite = prop.CanWrite,
                        Value = IsSimple(prop.PropertyType) ? value : (value?.ToString())
                    };
                }
            }
        }

        public IEnumerable<MethodDescriptor> GetMethods()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skip = new HashSet<string> { "Equals", "GetHashCode", "GetType", "ToString", "GetInstanceID",
                "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren",
                "SendMessage", "BroadcastMessage", "SendMessageUpwards", "CompareTag" };

            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
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
        }

        private bool TryGetGameObjectProperty(string name, string? remainder, out object? result)
        {
            result = null;
            switch (name.ToLowerInvariant())
            {
                case "name": result = _go.name; return remainder == null;
                case "tag": result = _go.tag; return remainder == null;
                case "layer": result = _go.layer; return remainder == null;
                case "activeself": result = _go.activeSelf; return remainder == null;
                case "activeinhierarchy": result = _go.activeInHierarchy; return remainder == null;
                default: return false;
            }
        }

        private bool TrySetGameObjectProperty(string propertyPath, object? value)
        {
            switch (propertyPath.ToLowerInvariant())
            {
                case "name": _go.name = value?.ToString() ?? ""; return true;
                case "tag": _go.tag = value?.ToString() ?? "Untagged"; return true;
                case "layer": _go.layer = Convert.ToInt32(value); return true;
                case "activeself": _go.SetActive(Convert.ToBoolean(value)); return true;
                default: return false;
            }
        }

        private bool TryResolveOnComponent(Component component, string propertyPath)
        {
            var firstSegment = propertyPath.Split('.')[0];
            var type = component.GetType();
            var prop = type.GetProperty(firstSegment,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null) return true;
            var field = type.GetField(firstSegment,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return field != null;
        }

        private static bool IsSimple(Type type)
        {
            return type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
                || type.IsEnum || type == typeof(Vector2) || type == typeof(Vector3)
                || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color)
                || type == typeof(Rect) || type == typeof(Bounds);
        }
    }
}
