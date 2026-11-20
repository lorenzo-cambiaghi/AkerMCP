#if TOOLS
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
            var node = ResolvePath(path);
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

            // A parent that did not resolve used to fall back to the scene root without a word: the
            // node landed where nobody asked, and every path the caller built on it was wrong.
            Node parent = root;
            if (!string.IsNullOrEmpty(parentPath))
                parent = ResolvePath(parentPath)
                    ?? throw new InvalidOperationException(
                        $"Parent not found: '{parentPath}'. Paths start at the edited scene root, e.g. '/{root.Name}/Child'.");

            var node = InstantiateNode(type);
            node.Name = string.IsNullOrEmpty(name) ? type : name;

            AddChildUndoable(parent, node, $"AkerMcp: Create {node.Name}");
            return new GodotSceneNode(node);
        }

        public bool DeleteNode(string path, bool recursive = true)
        {
            var node = ResolvePath(path);
            if (node == null) return false;

            var root = SceneRoot;
            if (root == null || node == root) return false; // can't delete the scene root this way
            var parent = node.GetParent();
            if (parent == null) return false;

            // One editor action, as the Scene dock's own Delete builds it: Ctrl+Z puts the node back,
            // and the scene is marked unsaved — before, nothing marked it, and closing Godot dropped
            // the deletion (and every other AI edit) without asking.
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction($"AkerMcp: Delete {node.Name}", UndoRedo.MergeMode.Disable, root);

            // remove_child clears the owner of every node it takes out of the scene, and a node
            // without the scene root as owner is not saved: undo has to hand the owners back, the
            // same list SceneTreeDock restores with _set_owners.
            int index = node.GetIndex();

            if (recursive)
            {
                undo.AddDoMethod(parent, Node.MethodName.RemoveChild, node);
                undo.AddUndoMethod(parent, Node.MethodName.AddChild, node, true);
                undo.AddUndoMethod(parent, Node.MethodName.MoveChild, node, index);
                foreach (var owned in OwnedBy(node, root, includeStart: true))
                    undo.AddUndoProperty(owned, Node.PropertyName.Owner, root);
            }
            else
            {
                // The children move up to the node's parent before it goes. Moving them by hand
                // (RemoveChild + AddChild + Owner on each direct child) re-owned only the direct
                // children: grandchildren kept a cleared owner and silently fell out of the saved
                // scene. Reparent keeps the world transform; the owners of the whole moved subtree
                // are then set explicitly, so the result does not hang on which 4.x Reparent
                // preserves them.
                var children = new List<Node>();
                foreach (Node child in node.GetChildren()) children.Add(child);
                var subtreeOwned = OwnedBy(node, root, includeStart: false);

                foreach (var child in children)
                    undo.AddDoMethod(child, Node.MethodName.Reparent, parent, true);
                foreach (var owned in subtreeOwned)
                    undo.AddDoProperty(owned, Node.PropertyName.Owner, root);
                undo.AddDoMethod(parent, Node.MethodName.RemoveChild, node);

                // Undo operations run in the order they are added (UndoRedo's default).
                undo.AddUndoMethod(parent, Node.MethodName.AddChild, node, true);
                undo.AddUndoMethod(parent, Node.MethodName.MoveChild, node, index);
                undo.AddUndoProperty(node, Node.PropertyName.Owner, root);
                foreach (var child in children)
                {
                    undo.AddUndoMethod(child, Node.MethodName.Reparent, node, true);
                    undo.AddUndoMethod(node, Node.MethodName.MoveChild, child, child.GetIndex());
                    // A child whose name clashed with a sibling under the new parent was renamed there.
                    undo.AddUndoProperty(child, Node.PropertyName.Name, child.Name);
                }
                foreach (var owned in subtreeOwned)
                    undo.AddUndoProperty(owned, Node.PropertyName.Owner, root);
            }

            // Out of the tree, the node now lives in the undo history; the history frees it when
            // the action can no longer be undone.
            undo.AddUndoReference(node);
            undo.CommitAction();
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

        /// <summary>
        /// Adds <paramref name="node"/> under <paramref name="parent"/> as one editor action owned by
        /// the edited scene: the node is saved with the scene, Ctrl+Z removes it, and the scene tab
        /// is marked unsaved. Shared by create and by the sprite/sound importers, so every node the
        /// AI places is undoable the same way.
        /// </summary>
        internal static void AddChildUndoable(Node parent, Node node, string actionName)
        {
            var root = SceneRoot
                ?? throw new InvalidOperationException("No scene is currently open in the editor.");

            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction(actionName, UndoRedo.MergeMode.Disable, root);
            // force_readable_name: a clashing name becomes "Box2", not "@Box@123" — the caller gets
            // a path it can actually type.
            undo.AddDoMethod(parent, Node.MethodName.AddChild, node, true);
            // Owner must be the scene root for the node to be saved with the scene and shown in
            // the editor's Scene dock.
            undo.AddDoProperty(node, Node.PropertyName.Owner, root);
            undo.AddDoReference(node);
            undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, node);
            undo.CommitAction();
        }

        /// <summary>
        /// Resolves a scene path the way every tool speaks it: "/Root/Child" or "/Child" (relative
        /// to the edited scene root), never Godot's absolute "/root/@EditorNode@…" tree path.
        /// </summary>
        internal static Node? ResolvePath(string? path)
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

        private static Node InstantiateNode(string type)
        {
            // Engine-native classes: ClassDB constructs them by name. ClassDB names are
            // case-sensitive; "sprite2d" is taken to mean Sprite2D rather than refused.
            var className = CanonicalClassName(type);
            if (className != null)
            {
                if (!ClassDB.CanInstantiate(className))
                    throw new ArgumentException($"'{className}' is an abstract Godot class and cannot be created.");

                var instance = ClassDB.Instantiate(className).AsGodotObject();
                if (instance is Node nativeNode)
                    return nativeNode;

                // Not a Node (a Resource, a plain Object): free what does not free itself.
                if (instance != null && instance is not RefCounted) instance.Free();
                throw new ArgumentException($"'{className}' is not a Node class, so it cannot be added to a scene.");
            }

            // C# / script types via the GodotSharp assembly.
            var resolved = GodotCapabilities.ResolveGodotType(type);
            if (resolved != null && typeof(Node).IsAssignableFrom(resolved) &&
                Activator.CreateInstance(resolved) is Node reflectedNode)
                return reflectedNode;

            // This used to fall back to a plain Node, so a typo created a node that looked like
            // a success and had none of the behaviour that was asked for.
            throw new ArgumentException(
                $"Unknown node type '{type}'. Use a Godot class name such as Node2D, Node3D, Sprite2D or MeshInstance3D.");
        }

        private static string? CanonicalClassName(string type)
        {
            if (ClassDB.ClassExists(type)) return type;
            foreach (var name in ClassDB.GetClassList())
                if (string.Equals(name, type, StringComparison.OrdinalIgnoreCase))
                    return name;
            return null;
        }

        private static bool MatchesType(Node node, string typeFilter)
        {
            // C# type chain first, so a script class (Player : CharacterBody2D) answers to its own
            // name and to every base.
            for (var t = node.GetType(); t != null && t != typeof(GodotObject); t = t.BaseType)
                if (t.Name.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                    return true;

            // Engine class hierarchy (e.g. "Light3D" matches OmniLight3D). Walked by hand because
            // Node.IsClass is case-sensitive: "light3d" matched nothing.
            for (string cls = node.GetClass(); !string.IsNullOrEmpty(cls); cls = ClassDB.GetParentClass(cls))
                if (cls.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        // The nodes of a subtree that the scene saves: those owned by the scene root. Nodes owned
        // by an instanced sub-scene keep that owner, which stays inside the subtree, so they are
        // left alone — the same set the Scene dock collects with get_owned_by.
        private static List<Node> OwnedBy(Node start, Node owner, bool includeStart)
        {
            var list = new List<Node>();
            foreach (var n in Traverse(start))
            {
                if (!includeStart && n == start) continue;
                if (n.Owner == owner) list.Add(n);
            }
            return list;
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
#endif
