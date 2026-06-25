#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Sprites;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// v1 (walking skeleton): loads the server-rasterized PNG as a runtime Texture and
    /// adds a Sprite entity to the editor preview game's scene, so it renders in the
    /// Scene view (and screenshots). This is RUNTIME-ONLY — it is not yet persisted as a
    /// .sdtex asset / Quantum scene entity (that asset-pipeline path is a follow-up,
    /// matching the Stride adapter's M1 status).
    ///
    /// Texture load + scene mutation must run on the GAME thread (like StrideScreenCapture),
    /// so we schedule via the game's ScriptSystem and block briefly for the result.
    /// Pivot is centered in v1; PixelsPerUnit is honored.
    /// </summary>
    public class StrideSpriteImporter : ISpriteImporter
    {
        public SpriteImportResult ImportSprite(SpriteImportRequest req)
        {
            var game = StrideSceneBridge.ActiveEditorGame();
            if (game == null)
                return new SpriteImportResult { Message = "No active Stride editor game; cannot import sprite." };

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            game.Script.AddTask(async () =>
            {
                try
                {
                    using var ms = new MemoryStream(req.Png);
                    var texture = Texture.Load(game.GraphicsDevice, ms);

                    var provider = new SpriteFromTexture
                    {
                        Texture = texture,
                        PixelsPerUnit = req.PixelsPerUnit,
                        IsTransparent = true,
                    };

                    var entity = new Entity(req.Name)
                    {
                        new SpriteComponent { SpriteProvider = provider }
                    };
                    entity.Transform.Position = new Vector3(req.PosX ?? 0f, req.PosY ?? 0f, req.PosZ ?? 0f);

                    game.SceneSystem.SceneInstance.RootScene.Entities.Add(entity);
                    tcs.TrySetResult(entity.Name);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult("ERROR:" + ex.Message);
                }
                await Task.CompletedTask;
            });

            string? res;
            try { res = tcs.Task.Wait(10000) ? tcs.Task.Result : null; }
            catch (Exception ex) { return new SpriteImportResult { Message = $"Sprite import failed: {ex.Message}" }; }

            if (res == null)
                return new SpriteImportResult { Message = "Sprite import timed out on the game thread." };
            if (res.StartsWith("ERROR:", StringComparison.Ordinal))
                return new SpriteImportResult { Message = "Sprite import failed: " + res.Substring(6) };

            return new SpriteImportResult
            {
                AssetPath = "(runtime, not persisted)",
                NodePath = res,
                Message = $"Loaded sprite '{req.Name}' as a runtime entity in the Stride preview scene " +
                          "(v1: not persisted as a .sdtex asset)."
            };
        }
    }
}
