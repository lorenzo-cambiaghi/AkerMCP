using System;

namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this can take a ready PNG (already
    /// rasterized server-side by the AI's shape-spec) and import it as a 2D sprite,
    /// optionally placing it in the scene. Engines that don't implement it report
    /// NOT_SUPPORTED. This is the inbound counterpart of <see cref="IScreenCapture"/>.
    /// </summary>
    public interface ISpriteImporter
    {
        /// <summary>Runs on the engine main thread (via the dispatcher).</summary>
        SpriteImportResult ImportSprite(SpriteImportRequest request);
    }

    public class SpriteImportRequest
    {
        public string Name { get; set; } = "sprite";

        /// <summary>Raw PNG bytes (RGBA) to import.</summary>
        public byte[] Png { get; set; } = Array.Empty<byte>();

        /// <summary>Engine pixels-per-unit for the imported sprite.</summary>
        public float PixelsPerUnit { get; set; } = 100f;

        /// <summary>Pivot in 0..1 (0,0 = bottom-left, 0.5,0.5 = center, in engine sprite convention).</summary>
        public float PivotX { get; set; } = 0.5f;
        public float PivotY { get; set; } = 0.5f;

        /// <summary>"smooth" (bilinear) or "point" (nearest).</summary>
        public string Filter { get; set; } = "smooth";

        /// <summary>If set, place a sprite node under this scene path after import.</summary>
        public string? ScenePath { get; set; }

        /// <summary>Optional world/local position for the placed node.</summary>
        public float? PosX { get; set; }
        public float? PosY { get; set; }
        public float? PosZ { get; set; }
    }

    public class SpriteImportResult
    {
        /// <summary>Engine asset path of the imported texture/sprite.</summary>
        public string AssetPath { get; set; } = "";

        /// <summary>Scene path of the placed node, if one was created.</summary>
        public string? NodePath { get; set; }

        public string Message { get; set; } = "";
    }
}
