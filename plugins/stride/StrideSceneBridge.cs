#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Engine;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Bridges <see cref="StrideSceneGraph"/> to the live entities of the scene
    /// currently open in Game Studio (Milestone 2, read-only).
    ///
    /// The path from the session to the runtime entities crosses non-public editor
    /// internals, so it is walked by reflection:
    ///   session.ServiceProvider → IAssetEditorsManager.EditorViewModels (internal)
    ///   → EntityHierarchyEditorViewModel.Controller (protected internal)
    ///   → controller.Game (EntityHierarchyEditorGame)
    ///   → game.ContentScene.Entities (public)
    /// Everything is defensive: any failure yields an empty hierarchy and a line in
    /// %TEMP%/akermcp-stride.log for diagnosis. MUST run on the WPF/editor thread
    /// (ISceneGraph calls are already marshalled there by the IPC handlers).
    /// </summary>
    public static class StrideSceneBridge
    {
        public static IEnumerable<Entity> GetRootEntities(SessionViewModel session)
        {
            try
            {
                var manager = session.ServiceProvider.Get<IAssetEditorsManager>();
                if (manager == null) { Log("no IAssetEditorsManager"); yield break; }

                var editorsProp = manager.GetType().GetProperty("EditorViewModels",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (editorsProp?.GetValue(manager) is not IEnumerable editors)
                {
                    Log("EditorViewModels not found/enumerable");
                    yield break;
                }

                foreach (var editor in editors)
                {
                    if (editor is not EntityHierarchyEditorViewModel ehevm) continue;

                    var scene = ContentSceneOf(ehevm);
                    if (scene == null) continue;

                    // The editor loads the edited scene as a CHILD scene of ContentScene,
                    // anchored by a "Virtual anchor of scene <guid>" marker entity. Walk the
                    // whole scene tree and skip those editor-only anchors so we surface the
                    // user's actual entities.
                    foreach (var entity in CollectRoots(scene))
                        yield return entity;

                    // First scene editor with a live ContentScene wins.
                    yield break;
                }

                Log("no EntityHierarchyEditor with a ContentScene found");
            }
            finally { }
        }

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

        private static Scene? ContentSceneOf(EntityHierarchyEditorViewModel editor)
        {
            try
            {
                // Controller is `protected internal virtual` on GameEditorViewModel
                // (a base of the entity-hierarchy editor view model).
                var controller = GetMemberUpChain(editor, "Controller");
                if (controller == null) { Log("Controller null"); return null; }

                var game = GetMemberUpChain(controller, "Game");
                if (game is not EntityHierarchyEditorGame ehGame) { Log($"Game not EntityHierarchyEditorGame ({game?.GetType().Name ?? "null"})"); return null; }

                var cs = ehGame.ContentScene; // public; null until the scene finishes loading
                if (cs != null)
                    Log($"ContentScene: {cs.Entities.Count} entities, {cs.Children.Count} child scenes");
                return cs;
            }
            catch (Exception ex)
            {
                Log($"ContentSceneOf failed: {ex.Message}");
                return null;
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
