#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AkerMcp.Shared.Abstraction;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Engine;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Read-only scene graph over the entities of the scene currently edited in
    /// Game Studio (Milestone 1).
    ///
    /// Reaching the live editor scene from a <see cref="SessionViewModel"/> goes
    /// through Game Studio's editor-game wiring, which is verified at runtime in a
    /// later milestone. Until then <see cref="RootEntitiesProvider"/> is the seam:
    /// when set it yields the scene's root entities, otherwise the hierarchy is
    /// empty (the IPC server, ping and capabilities still work end-to-end).
    /// </summary>
    public sealed class StrideSceneGraph : ISceneGraph
    {
        /// <summary>Set by the editor-game bridge once the live scene is reachable.</summary>
        public static Func<IEnumerable<Entity>>? RootEntitiesProvider;

        private readonly SessionViewModel _session;

        public StrideSceneGraph(SessionViewModel session) => _session = session;

        private static IEnumerable<Entity> RootEntities()
            => RootEntitiesProvider?.Invoke() ?? Array.Empty<Entity>();

        public IEnumerable<ISceneNode> GetRootNodes()
        {
            foreach (var entity in RootEntities())
                yield return new StrideSceneNode(entity);
        }

        public ISceneNode? GetNode(string path)
        {
            var entity = FindByPath(path);
            return entity != null ? new StrideSceneNode(entity) : null;
        }

        public IEnumerable<ISceneNode> Query(QueryFilter filter)
        {
            int count = 0;
            foreach (var entity in TraverseAll())
            {
                if (count >= filter.MaxResults) yield break;

                if (filter.TypeFilter != null && !MatchesType(entity, filter.TypeFilter))
                    continue;
                if (filter.NamePattern != null &&
                    !Regex.IsMatch(entity.Name ?? "", WildcardToRegex(filter.NamePattern), RegexOptions.IgnoreCase))
                    continue;

                count++;
                yield return new StrideSceneNode(entity);
            }
        }

        public ISceneNode CreateNode(string type, string? name, string? parentPath)
            => throw new NotSupportedException("Scene editing is not available yet in the Stride adapter (read-only milestone).");

        public bool DeleteNode(string path, bool recursive = true)
            => throw new NotSupportedException("Scene editing is not available yet in the Stride adapter (read-only milestone).");

        public int GetTotalNodeCount()
        {
            int count = 0;
            foreach (var _ in TraverseAll()) count++;
            return count;
        }

        private static IEnumerable<Entity> TraverseAll()
        {
            foreach (var root in RootEntities())
                foreach (var e in Traverse(root))
                    yield return e;
        }

        private static IEnumerable<Entity> Traverse(Entity entity)
        {
            yield return entity;
            foreach (var childTransform in entity.Transform.Children)
                foreach (var descendant in Traverse(childTransform.Entity))
                    yield return descendant;
        }

        private static Entity? FindByPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.TrimStart('/').Split('/');
            if (parts.Length == 0) return null;

            Entity? current = null;
            IEnumerable<Entity> level = RootEntities();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                current = null;
                foreach (var e in level)
                {
                    if (e.Name == part) { current = e; break; }
                }
                if (current == null) return null;
                level = ChildEntities(current);
            }
            return current;
        }

        private static IEnumerable<Entity> ChildEntities(Entity entity)
        {
            foreach (var childTransform in entity.Transform.Children)
                yield return childTransform.Entity;
        }

        private static bool MatchesType(Entity entity, string typeFilter)
        {
            foreach (var component in entity.Components)
            {
                if (component.GetType().Name.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string WildcardToRegex(string pattern)
            => "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    }
}
