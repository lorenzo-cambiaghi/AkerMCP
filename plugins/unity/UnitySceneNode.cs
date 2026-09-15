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

            // A component named by its type: the form the guide documents and inspect lists
            if (TrySplitComponentPrefix(propertyPath, out var named, out var member) && TryResolveOnComponent(named, member))
                return _resolver.Resolve(named, member);

            // Try Transform first (most common)
            if (TryResolveOnComponent(_go.transform, propertyPath))
                return _resolver.Resolve(_go.transform, propertyPath);

            // Try each component
            foreach (var component in _go.GetComponents<Component>())
            {
                if (component == null || component is Transform) continue;
                if (TryResolveOnComponent(component, propertyPath))
                {
                    try { return _resolver.Resolve(component, propertyPath); }
                    catch (PropertyPathException) { continue; }
                }
            }

            throw new PropertyPathException(
                $"Property '{propertyPath}' not found on '{Name}' or any of its components");
        }

        public void SetProperty(string propertyPath, object? value)
        {
            // Direct GameObject properties
            if (TrySetGameObjectProperty(propertyPath, value))
                return;

            // A component named by its type: the form the guide documents and inspect lists
            if (TrySplitComponentPrefix(propertyPath, out var named, out var member) && TryResolveOnComponent(named, member))
            {
                _resolver.Set(named, member, value);
                return;
            }

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
                    try
                    {
                        _resolver.Set(component, propertyPath, value);
                        return;
                    }
                    catch (PropertyPathException) { continue; }
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
                    if (!(component is Transform) && IsInheritedByEveryComponent(prop.DeclaringType)) continue;
                    var displayName = prefix + prop.Name;
                    if (!seen.Add(displayName)) continue;

                    object? value = null;
                    bool copies = GetterMakesACopy(prop);
                    try { if (prop.CanRead && !copies) value = prop.GetValue(component); } catch { }

                    yield return new PropertyDescriptor
                    {
                        Name = displayName,
                        TypeName = prop.PropertyType.Name,
                        CanRead = prop.CanRead,
                        CanWrite = prop.CanWrite,
                        Value = copies ? NotReadCopies : WireValue(prop.PropertyType, value)
                    };
                }

                // Public fields are what a MonoBehaviour shows in its inspector, and get_property/set_property already
                // resolve them; inspect used to list only properties, so the values that matter most could be read
                // but not discovered.
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var displayName = prefix + field.Name;
                    if (!seen.Add(displayName)) continue;

                    object? value = null;
                    try { value = field.GetValue(component); } catch { }

                    yield return new PropertyDescriptor
                    {
                        Name = displayName,
                        TypeName = field.FieldType.Name,
                        CanRead = true,
                        CanWrite = !field.IsInitOnly,
                        Value = WireValue(field.FieldType, value)
                    };
                }
            }
        }

        private const string NotReadCopies = "(not read: this getter makes a copy in the scene; read the shared member)";

        /// <summary>
        /// Members every component inherits from UnityEngine.Object, Component and MonoBehaviour — name, tag,
        /// gameObject, transform, hideFlags, runInEditMode, destroyCancellationToken: the same value on every component,
        /// repeated once per component, and enough of them to push a GameObject past the inspect output cap.
        /// Behaviour's own members (enabled) stay: they differ per component.
        /// </summary>
        private static bool IsInheritedByEveryComponent(Type? declaringType) =>
            declaringType == typeof(UnityEngine.Object) || declaringType == typeof(Component) || declaringType == typeof(MonoBehaviour);

        /// <summary>
        /// Getters that instantiate what they return: in edit mode Renderer.material and MeshFilter.mesh leak a copy into
        /// the scene (and Unity logs an error), so an inspection — a read — would change the scene it describes.
        /// </summary>
        private static bool GetterMakesACopy(PropertyInfo prop)
        {
            var owner = prop.DeclaringType;
            if (owner == typeof(Renderer)) return prop.Name == "material" || prop.Name == "materials";
            if (owner == typeof(MeshFilter)) return prop.Name == "mesh";
            if (owner == typeof(Collider)) return prop.Name == "material";
            return false;
        }

        /// <summary>
        /// Strings and value types as they are — the client writes structs as JSON through its serializer, which guards
        /// against the properties that return their own type — and anything else (references to objects) as text.
        /// </summary>
        private static object? WireValue(Type declaredType, object? value) =>
            declaredType.IsValueType || declaredType == typeof(string) ? value : value?.ToString();

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

        /// <summary>
        /// The component a path names by its type ("Camera.fieldOfView", "MeshRenderer.material.color") and the rest of
        /// the path. The server's guide documents this form and <see cref="GetProperties"/> lists names in it, but the
        /// node used to look for a member CALLED "Camera" on every component: nothing inspect listed could be read back,
        /// only the bare "fieldOfView". The first component of that type wins, as on Stride.
        /// </summary>
        private bool TrySplitComponentPrefix(string propertyPath, out Component component, out string memberPath)
        {
            component = null!;
            memberPath = "";
            int dot = propertyPath.IndexOf('.');
            if (dot <= 0 || dot == propertyPath.Length - 1) return false;

            var prefix = propertyPath.Substring(0, dot);
            foreach (var candidate in _go.GetComponents<Component>())
            {
                if (candidate == null || !string.Equals(candidate.GetType().Name, prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                component = candidate;
                memberPath = propertyPath.Substring(dot + 1);
                return true;
            }
            return false;
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
    }
}
