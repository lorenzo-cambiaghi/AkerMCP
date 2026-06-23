#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    public class GodotSceneGraph : ISceneGraph
    {
        // The currently edited scene's root node (null if no scene is open).
        private static Node? SceneRoot => EditorInterface.Singleton.GetEditedSceneRoot();

        public ISceneNode? GetNode(string path)
        {
            var node = FindByPath(path);
            return node != null ? new GodotSceneNode(node) : null;
        }

        public IEnumerable<ISceneNode> GetRootNodes()
        {
            // Unlike Unity, a Godot scene has exactly one root node.
            var root = SceneRoot;
            if (root != null)
                yield return new GodotSceneNode(root);
        }

        public IEnumerable<ISceneNode> Query(QueryFilter filter)
        {
            var root = SceneRoot;
            if (root == null) yield break;

            int count = 0;
            foreach (var node in Traverse(root))
            {
                if (count >= filter.MaxResults) yield break;

                if (filter.TypeFilter != null && !MatchesType(node, filter.TypeFilter))
                    continue;

                if (filter.NamePattern != null &&
                    !Regex.IsMatch(node.Name.ToString(), WildcardToRegex(filter.NamePattern), RegexOptions.IgnoreCase))
                    continue;

                // Godot has no tags; groups are the closest analog.
                if (filter.Tag != null && !node.IsInGroup(filter.Tag))
                    continue;

                count++;
                yield return new GodotSceneNode(node);
            }
        }

        public ISceneNode CreateNode(string type, string? name, string? parentPath)
        {
            var root = SceneRoot
                ?? throw new InvalidOperationException("No scene is currently open in the editor.");

            var node = InstantiateNode(type);
            node.Name = name ?? type;

            Node? parent = string.IsNullOrEmpty(parentPath) ? root : FindByPath(parentPath);
            parent ??= root;

            parent.AddChild(node);
            // Owner must be the scene root for the node to be saved with the scene
            // and shown in the editor's Scene dock.
            node.Owner = root;

            return new GodotSceneNode(node);
        }

        public bool DeleteNode(string path, bool recursive = true)
        {
            var node = FindByPath(path);
            if (node == null) return false;

            var root = SceneRoot;
            if (node == root) return false; // can't delete the scene root this way

            if (!recursive)
            {
                // Re-parent children to this node's parent before freeing it.
                var parent = node.GetParent();
                var children = new List<Node>();
                foreach (Node child in node.GetChildren()) children.Add(child);
                foreach (var child in children)
                {
                    node.RemoveChild(child);
                    parent?.AddChild(child);
                    if (root != null) child.Owner = root;
                }
            }

            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
            return true;
        }

        public int GetTotalNodeCount()
        {
            var root = SceneRoot;
            if (root == null) return 0;
            int count = 0;
            foreach (var _ in Traverse(root)) count++;
            return count;
        }

        private static Node InstantiateNode(string type)
        {
            // Engine-native classes: ClassDB constructs them by name.
            if (ClassDB.ClassExists(type) && ClassDB.CanInstantiate(type))
            {
                if (ClassDB.Instantiate(type).As<Node>() is Node nativeNode)
                    return nativeNode;
            }

            // C# / script types via the GodotSharp assembly.
            var resolved = GodotCapabilities.ResolveGodotType(type);
            if (resolved != null && typeof(Node).IsAssignableFrom(resolved) &&
                Activator.CreateInstance(resolved) is Node reflectedNode)
                return reflectedNode;

            // Fallback: a plain Node so creation never hard-fails.
            return new Node();
        }

        private static bool MatchesType(Node node, string typeFilter)
        {
            if (node.GetType().Name.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                return true;
            // Engine class hierarchy check (e.g. "Light3D" matches OmniLight3D).
            try { return node.IsClass(typeFilter); }
            catch { return false; }
        }

        private static Node? FindByPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var root = SceneRoot;
            if (root == null) return null;

            var parts = path.TrimStart('/').Split('/');
            if (parts.Length == 0) return null;

            // The first segment may be the scene root's own name (e.g. "/Main/Player")
            // or omitted (e.g. "/Player" relative to the root).
            int startIndex = root.Name.ToString().Equals(parts[0]) ? 1 : 0;

            Node current = root;
            for (int i = startIndex; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                Node? child = null;
                foreach (Node c in current.GetChildren())
                {
                    if (c.Name.ToString() == parts[i]) { child = c; break; }
                }
                if (child == null) return null;
                current = child;
            }
            return current;
        }

        private static IEnumerable<Node> Traverse(Node node)
        {
            yield return node;
            foreach (Node child in node.GetChildren())
                foreach (var descendant in Traverse(child))
                    yield return descendant;
        }

        private static string WildcardToRegex(string pattern)
            => "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    }
}
