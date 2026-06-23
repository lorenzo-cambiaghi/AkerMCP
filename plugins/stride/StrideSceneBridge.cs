#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Services;
using Stride.Core.Quantum;
using Stride.Engine;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Bridges <see cref="StrideSceneGraph"/>/<see cref="StrideSceneNode"/> to Game
    /// Studio's open scene. Reads use the live (game-side) entities of the editor's
    /// ContentScene; writes go through the ASSET-side model + Quantum so they are
    /// undoable and persisted (game-side & asset-side share the entity Id).
    ///
    /// The route to the editor crosses non-public internals (walked by reflection):
    ///   session.ServiceProvider → IAssetEditorsManager.EditorViewModels (internal)
    ///   → EntityHierarchyEditorViewModel.Controller (protected internal)
    ///   → controller.Game → EntityHierarchyEditorGame.ContentScene (public).
    /// All scene access runs on the WPF/editor thread (marshalled by the IPC handlers).
    /// </summary>
    public static class StrideSceneBridge
    {
        /// <summary>Set by <see cref="StrideEnginePlugin"/> when a session opens.</summary>
        public static SessionViewModel? Session;

        // --- reads ---------------------------------------------------------------

        public static IEnumerable<Entity> GetRootEntities()
        {
            var editor = FindActiveEntityEditor();
            if (editor == null) { Log("no active EntityHierarchyEditor"); yield break; }

            var scene = ContentSceneOf(editor);
            if (scene == null) yield break;

            // The edited scene is a CHILD scene of ContentScene, anchored by a
            // "Virtual anchor of scene <guid>" marker entity (skipped).
            foreach (var entity in CollectRoots(scene))
                yield return entity;
        }

        // --- writes (Quantum, undoable) -----------------------------------------

        public static void SetEntityProperty(Guid entityId, string propertyPath, object? value)
        {
            var (session, _, asset) = RequireSceneContext();
            if (!asset.Hierarchy.Parts.TryGetValue(entityId, out var design))
                throw new InvalidOperationException($"Entity {entityId} not found in the edited asset.");

            var assetEntity = design.Entity;
            var (componentSelector, memberPath) = SplitPath(propertyPath);

            var target = ResolveComponent(assetEntity, componentSelector)
                ?? throw new InvalidOperationException($"Component '{componentSelector}' not found on entity '{assetEntity.Name}'.");

            var node = session.AssetNodeContainer.GetOrCreateNode(target) as IObjectNode
                ?? throw new InvalidOperationException("Could not resolve a Quantum node for the target component.");

            // memberPath is "Position" or one level of struct nesting like "Position.X".
            var segments = memberPath.Split('.');
            if (segments.Length > 2)
                throw new NotSupportedException(
                    $"Only one level of struct nesting is supported (e.g. 'Transform.Position.X'); '{propertyPath}' is deeper.");

            var memberNode = node.TryGetChild(segments[0])
                ?? throw new InvalidOperationException($"Member '{segments[0]}' not found on {target.GetType().Name}.");

            var undo = session.ServiceProvider.Get<IUndoRedoService>();
            using (var tx = undo.CreateTransaction())
            {
                if (segments.Length == 1)
                {
                    memberNode.Update(Coerce(value, memberNode.Type));
                }
                else
                {
                    // Sub-field of a value-type member (e.g. Vector3.X): read the whole
                    // struct, mutate the boxed copy by reflection, then write it back.
                    var boxed = memberNode.Retrieve()
                        ?? throw new InvalidOperationException($"Cannot read current value of '{segments[0]}'.");
                    SetStructField(boxed, segments[1], value);
                    memberNode.Update(boxed);
                }
                undo.SetName(tx, $"Set {propertyPath} on {assetEntity.Name}");
            }
        }

        private static void SetStructField(object boxedStruct, string fieldName, object? value)
        {
            var t = boxedStruct.GetType();
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (f != null) { f.SetValue(boxedStruct, Coerce(value, f.FieldType)); return; }
            var p = t.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null && p.CanWrite) { p.SetValue(boxedStruct, Coerce(value, p.PropertyType)); return; }
            throw new InvalidOperationException($"Sub-field '{fieldName}' not found on {t.Name}.");
        }

        public static Entity CreateEntity(string name, Guid? parentId)
        {
            var (session, editor, asset) = RequireSceneContext();
            var graph = HierarchyGraph(editor);

            Entity? parent = null;
            if (parentId is Guid pid)
            {
                if (!asset.Hierarchy.Parts.TryGetValue(pid, out var pdesign))
                    throw new InvalidOperationException($"Parent entity {pid} not found in the edited asset.");
                parent = pdesign.Entity;
            }

            var entity = new Entity { Name = name };
            var design = new EntityDesign(entity);
            var collection = new AssetPartCollection<EntityDesign, Entity>();
            collection.Add(design);
            int index = parent == null ? asset.Hierarchy.RootParts.Count : parent.Transform.Children.Count;

            var undo = session.ServiceProvider.Get<IUndoRedoService>();
            using (var tx = undo.CreateTransaction())
            {
                graph.AddPartToAsset(collection, design, parent, index);
                undo.SetName(tx, $"Create entity {name}");
            }
            return entity;
        }

        public static bool DeleteEntity(Guid entityId)
        {
            var (session, editor, asset) = RequireSceneContext();
            if (!asset.Hierarchy.Parts.TryGetValue(entityId, out var design))
                return false;

            var graph = HierarchyGraph(editor);
            var undo = session.ServiceProvider.Get<IUndoRedoService>();
            using (var tx = undo.CreateTransaction())
            {
                graph.RemovePartFromAsset(design); // removes the entity and its subtree
                undo.SetName(tx, $"Delete entity {design.Entity.Name}");
            }
            return true;
        }

        private static (SessionViewModel session, EntityHierarchyEditorViewModel editor, EntityHierarchyAssetBase asset) RequireSceneContext()
        {
            var session = Session ?? throw new InvalidOperationException("No active Stride session.");
            var editor = FindActiveEntityEditor() ?? throw new InvalidOperationException("No scene editor is open.");
            if (editor.Asset.Asset is not EntityHierarchyAssetBase asset)
                throw new InvalidOperationException("The active editor is not a scene/prefab asset.");
            return (session, editor, asset);
        }

        private static AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity> HierarchyGraph(EntityHierarchyEditorViewModel editor)
            => editor.Asset.PropertyGraph as AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>
               ?? throw new InvalidOperationException("Asset property graph is not an entity hierarchy graph.");

        // --- editor navigation ---------------------------------------------------

        internal static EntityHierarchyEditorViewModel? FindActiveEntityEditor()
        {
            try
            {
                var manager = Session?.ServiceProvider.Get<IAssetEditorsManager>();
                if (manager == null) { Log("no IAssetEditorsManager"); return null; }

                var editorsProp = manager.GetType().GetProperty("EditorViewModels",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (editorsProp?.GetValue(manager) is not IEnumerable editors)
                {
                    Log("EditorViewModels not found/enumerable");
                    return null;
                }

                foreach (var editor in editors)
                    if (editor is EntityHierarchyEditorViewModel ehevm)
                        return ehevm;

                return null;
            }
            catch (Exception ex) { Log($"FindActiveEntityEditor failed: {ex.Message}"); return null; }
        }

        /// <summary>The live editor preview Game of the active scene editor, or null.</summary>
        internal static Game? ActiveEditorGame()
        {
            try
            {
                var editor = FindActiveEntityEditor();
                if (editor == null) return null;
                var controller = GetMemberUpChain(editor, "Controller");
                if (controller == null) return null;
                return GetMemberUpChain(controller, "Game") as Game;
            }
            catch (Exception ex) { Log($"ActiveEditorGame failed: {ex.Message}"); return null; }
        }

        private static Scene? ContentSceneOf(EntityHierarchyEditorViewModel editor)
        {
            try
            {
                var controller = GetMemberUpChain(editor, "Controller");
                if (controller == null) { Log("Controller null"); return null; }

                var game = GetMemberUpChain(controller, "Game");
                if (game is not EntityHierarchyEditorGame ehGame)
                {
                    Log($"Game not EntityHierarchyEditorGame ({game?.GetType().Name ?? "null"})");
                    return null;
                }

                var cs = ehGame.ContentScene; // public; null until the scene finishes loading
                if (cs != null)
                    Log($"ContentScene: {cs.Entities.Count} entities, {cs.Children.Count} child scenes");
                return cs;
            }
            catch (Exception ex) { Log($"ContentSceneOf failed: {ex.Message}"); return null; }
        }

        // --- helpers -------------------------------------------------------------

        private static IEnumerable<Entity> CollectRoots(Scene scene)
        {
            foreach (var e in scene.Entities)
            {
                if (e.Name != null && e.Name.StartsWith("Virtual anchor of scene", StringComparison.Ordinal))
                    continue; // editor-only marker
                yield return e;
            }
            foreach (var child in scene.Children)
                foreach (var e in CollectRoots(child))
                    yield return e;
        }

        private static (string component, string member) SplitPath(string path)
        {
            int i = path.IndexOf('.');
            // No component prefix → treat as a Transform member (e.g. "Position").
            return i < 0 ? ("Transform", path) : (path.Substring(0, i), path.Substring(i + 1));
        }

        private static object? ResolveComponent(Entity entity, string selector)
        {
            if (selector is "Transform" or "TransformComponent")
                return entity.Transform;

            foreach (var c in entity.Components)
            {
                var n = c.GetType().Name;
                if (n == selector || n == selector + "Component")
                    return c;
            }
            return null;
        }

        private static object? Coerce(object? value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            try
            {
                if (underlying.IsEnum)
                    return Enum.Parse(underlying, value.ToString() ?? "", ignoreCase: true);
                return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            }
            catch
            {
                // Leave as-is and let Quantum's Update surface a precise type error.
                return value;
            }
        }

        // Reflection does not surface inherited non-public members, so walk the
        // declaring-type chain to find a (possibly protected/internal) property.
        private static object? GetMemberUpChain(object instance, string name)
        {
            for (var t = instance.GetType(); t != null; t = t.BaseType)
            {
                var prop = t.GetProperty(name,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null) return prop.GetValue(instance);
            }
            return null;
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "akermcp-stride.log"),
                    $"{DateTime.Now:HH:mm:ss} [scene-bridge] {msg}{Environment.NewLine}");
            }
            catch { /* diagnostics must never throw */ }
        }
    }
}
