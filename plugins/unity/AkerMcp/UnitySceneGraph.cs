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

        public ISceneNode CreateNode(string type, string? name, string? parentPath)
        {
            var go = new GameObject(name ?? type);

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = FindByPath(parentPath);
                if (parent != null)
                    go.transform.SetParent(parent.transform, false);
            }

            var componentType = UnityCapabilities.ResolveUnityType(type);
            if (componentType != null && typeof(Component).IsAssignableFrom(componentType) && componentType != typeof(Transform))
            {
                go.AddComponent(componentType);
            }

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
                // deleted object's parent before destroying the node itself.
                var parent = go.transform.parent;
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                {
                    var child = go.transform.GetChild(i);
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

            var cleanPath = path.TrimStart('/');
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
