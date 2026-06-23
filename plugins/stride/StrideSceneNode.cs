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
            => throw new NotSupportedException("Setting properties is not available yet in the Stride adapter (read-only milestone).");

        public object? CallMethod(string methodName, object?[]? args)
            => throw new NotSupportedException("Calling methods is not available yet in the Stride adapter (read-only milestone).");

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
