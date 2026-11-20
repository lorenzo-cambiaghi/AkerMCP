#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    public class UnitySceneGraph : ISceneGraph
    {
        public ISceneNode? GetNode(string path)
        {
            var go = FindByPath(path);
            if (go == null) return null;

            // Unity fake null check
            if (go is UnityEngine.Object uObj && uObj == null) return null;

            return new UnitySceneNode(go);
        }

        public IEnumerable<ISceneNode> GetRootNodes()
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var go in scene.GetRootGameObjects())
                yield return new UnitySceneNode(go);
        }

        public IEnumerable<ISceneNode> Query(QueryFilter filter)
        {
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int count = 0;

            foreach (var go in allObjects)
            {
                if (count >= filter.MaxResults) yield break;

                if (filter.TypeFilter != null)
                {
                    bool hasType = false;
                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp != null && comp.GetType().Name.Equals(filter.TypeFilter, System.StringComparison.OrdinalIgnoreCase))
                        {
                            hasType = true;
                            break;
                        }
                    }
                    if (!hasType && !go.GetType().Name.Equals(filter.TypeFilter, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (filter.NamePattern != null)
                {
                    if (!Regex.IsMatch(go.name, WildcardToRegex(filter.NamePattern), RegexOptions.IgnoreCase))
                        continue;
                }

                if (filter.Tag != null)
                {
                    if (!go.CompareTag(filter.Tag))
                        continue;
                }

                count++;
                yield return new UnitySceneNode(go);
            }
        }

        /// <summary>
        /// Creates a GameObject: empty, a primitive shape, or carrying the component <paramref name="type"/> names.
        /// <para>
        /// Every failure throws before anything is left in the scene. It used to succeed regardless: an unknown type
        /// gave an empty GameObject named after it, a missing parent put the object at the scene root, and both were
        /// reported as "created" — so the caller went on configuring a component that did not exist.
        /// </para>
        /// </summary>
        public ISceneNode CreateNode(string type, string? name, string? parentPath)
        {
            Transform? parent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentObject = FindByPath(parentPath)
                    ?? throw new System.InvalidOperationException(
                        $"Parent '{parentPath}' not found in the active scene; nothing was created.");
                parent = parentObject.transform;
            }

            GameObject go;
            if (UnityCapabilities.TryGetPrimitive(type, out var primitive))
            {
                go = GameObject.CreatePrimitive(primitive);
                go.name = name ?? type;
            }
            else if (UnityCapabilities.IsEmptyObjectType(type))
            {
                go = new GameObject(name ?? "GameObject");
            }
            else
            {
                var componentType = UnityCapabilities.ResolveComponentType(type, out var problem)
                    ?? throw new System.InvalidOperationException(problem + " Nothing was created.");

                go = new GameObject(name ?? type);
                try
                {
                    // Unity refuses some types by logging and returning null (an editor-only script, a component the
                    // GameObject cannot take) rather than throwing.
                    if (go.AddComponent(componentType) == null)
                        throw new System.InvalidOperationException(
                            $"Unity refused to add {componentType.FullName} (the Console says why); nothing was created.");
                }
                catch
                {
                    Object.DestroyImmediate(go);
                    throw;
                }
            }

            if (parent != null)
                go.transform.SetParent(parent, false);

#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, $"MCP: Create {type}");
#endif

            return new UnitySceneNode(go);
        }

        public bool DeleteNode(string path, bool recursive = true)
        {
            var go = FindByPath(path);
            if (go == null) return false;

            if (!recursive)
            {
                // Preserve children: re-parent them (keeping world position) to the
                // deleted object's parent before destroying the node itself. In their
                // own order: each one lands last under the new parent, so walking them
                // backwards reversed them.
                var parent = go.transform.parent;
                var children = new List<Transform>(go.transform.childCount);
                foreach (Transform c in go.transform) children.Add(c);
                foreach (var child in children)
                {
#if UNITY_EDITOR
                    UnityEditor.Undo.SetTransformParent(child, parent, "MCP: Detach child before delete");
#else
                    child.SetParent(parent, true);
#endif
                }
            }

#if UNITY_EDITOR
            UnityEditor.Undo.DestroyObjectImmediate(go);
#else
            Object.DestroyImmediate(go);
#endif
            return true;
        }

        public int GetTotalNodeCount()
        {
            return Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
        }

        private static GameObject? FindByPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var cleanPath = path!.TrimStart('/'); // Unity's IsNullOrEmpty carries no [NotNullWhen]
            var parts = cleanPath.Split('/');

            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            var root = roots.FirstOrDefault(r => r.name == parts[0]);
            if (root == null) return null;

            var current = root;
            for (int i = 1; i < parts.Length; i++)
            {
                Transform? child = null;
                for (int j = 0; j < current.transform.childCount; j++)
                {
                    var c = current.transform.GetChild(j);
                    if (c.name == parts[i])
                    {
                        child = c;
                        break;
                    }
                }
                if (child == null) return null;
                current = child.gameObject;
            }

            return current;
        }

        private static string WildcardToRegex(string pattern)
        {
            return "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        }
    }
}
