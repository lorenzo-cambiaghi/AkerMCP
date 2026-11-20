#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Reflection;
using Stride.Engine;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Wraps a Stride <see cref="Entity"/>. Stride has a real component model
    /// (an entity owns <see cref="EntityComponent"/>s), so components are surfaced
    /// from <c>entity.Components</c> and the hierarchy follows the transform tree.
    /// Milestone 1 is read-only: writes throw NotSupported until scene editing
    /// (through the asset/Quantum layer, for undo + persistence) is wired up.
    /// </summary>
    public sealed class StrideSceneNode : ISceneNode
    {
        private readonly Entity _entity;
        private readonly PropertyPathResolver _resolver = new();

        public StrideSceneNode(Entity entity) => _entity = entity;

        public string Name => _entity.Name ?? "Entity";
        public string TypeName => nameof(Entity);
        public object UnderlyingObject => _entity;

        public string Path
        {
            get
            {
                var names = new List<string>();
                var t = _entity.Transform;
                while (t != null)
                {
                    names.Insert(0, t.Entity.Name ?? "Entity");
                    t = t.Parent;
                }
                return "/" + string.Join("/", names);
            }
        }

        public ISceneNode? Parent
        {
            get
            {
                var parent = _entity.Transform.Parent;
                return parent != null ? new StrideSceneNode(parent.Entity) : null;
            }
        }

        public IEnumerable<ISceneNode> Children
        {
            get
            {
                foreach (var childTransform in _entity.Transform.Children)
                    yield return new StrideSceneNode(childTransform.Entity);
            }
        }

        public object? GetProperty(string propertyPath)
        {
            if (propertyPath.Equals("name", StringComparison.OrdinalIgnoreCase))
                return _entity.Name;
            return _resolver.Resolve(_entity, propertyPath);
        }

        public void SetProperty(string propertyPath, object? value)
            => StrideSceneBridge.SetEntityProperty(_entity.Id, propertyPath, Typed(propertyPath, value));

        /// <summary>
        /// The value as the member's type when it arrives untyped. set_property hands a value over as its JSON text when the
        /// entity does not declare the path ("Position" and "Model.X" name a component's member), and the bridge coerces
        /// with Convert.ChangeType, which reads primitives only: a vector or an enum given that way never arrived. The
        /// component is resolved as the bridge resolves it.
        /// </summary>
        private object? Typed(string propertyPath, object? value)
        {
            if (!(value is string) && !(value is System.Text.Json.JsonElement)) return value;

            int dot = propertyPath.IndexOf('.');
            var selector = dot < 0 ? "Transform" : propertyPath.Substring(0, dot);
            var member = dot < 0 ? propertyPath : propertyPath.Substring(dot + 1);

            object? component = selector is "Transform" or "TransformComponent" ? _entity.Transform : null;
            if (component == null)
                foreach (var candidate in _entity.Components)
                {
                    var name = candidate.GetType().Name;
                    if (name == selector || name == selector + "Component") { component = candidate; break; }
                }

            var type = component == null ? null : _resolver.GetTargetType(component.GetType(), member);
            return type == null ? value : PropertyPathResolver.ConvertValue(value, type);
        }

        // Fully qualified: net10 has a System.Reflection.MethodInvoker of its own.
        private static readonly AkerMcp.Shared.Reflection.MethodInvoker Invoker = new();

        /// <summary>
        /// The method on the first component with an overload the arguments fit (in Stride most callable behaviour lives on
        /// components), then on the entity; or, written "ModelComponent.Method" (or "Model.Method"), on that component
        /// only. The overload is chosen once, by the invoker: the pre-check here used GetMethod(name, IgnoreCase), which
        /// throws AmbiguousMatchException on any overloaded name.
        /// </summary>
        public object? CallMethod(string methodName, object?[]? args)
        {
            int dot = methodName.IndexOf('.');
            if (dot > 0 && dot < methodName.Length - 1 && methodName.IndexOf('.', dot + 1) < 0)
            {
                var prefix = methodName.Substring(0, dot);
                foreach (var component in _entity.Components)
                {
                    var typeName = component.GetType().Name;
                    if (string.Equals(typeName, prefix, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(typeName, prefix + "Component", StringComparison.OrdinalIgnoreCase))
                        return Invoker.Invoke(new object[] { component }, methodName.Substring(dot + 1), args);
                }
            }

            var targets = new List<object>();
            foreach (var component in _entity.Components) targets.Add(component);
            targets.Add(_entity);
            return Invoker.Invoke(targets, methodName, args);
        }

        public IEnumerable<ComponentInfo> GetComponents()
        {
            foreach (var component in _entity.Components)
            {
                var type = component.GetType();
                yield return new ComponentInfo
                {
                    Name = type.Name,
                    FullTypeName = type.FullName ?? type.Name,
                    Enabled = true
                };
            }
        }

        public IEnumerable<PropertyDescriptor> GetProperties()
        {
            yield return new PropertyDescriptor
            {
                Name = "name", TypeName = "string", CanRead = true, CanWrite = false,
                Value = _entity.Name
            };

            // One read-only summary row per component, so `inspect` shows the
            // entity's composition without diving into every field yet.
            foreach (var component in _entity.Components)
            {
                var type = component.GetType();
                yield return new PropertyDescriptor
                {
                    Name = type.Name,
                    TypeName = type.Name,
                    CanRead = true,
                    CanWrite = false,
                    Value = type.Name
                };
            }
        }

        public IEnumerable<MethodDescriptor> GetMethods()
            => Enumerable.Empty<MethodDescriptor>();
    }
}
