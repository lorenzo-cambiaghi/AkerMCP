namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this can create, open, and save scenes.
    /// Engines that don't implement it report that scene management is unavailable.
    /// (Scene creation is trivial via `execute` on Unity but awkward on Godot/Stride, so
    /// a dedicated engine-neutral capability is worthwhile.)
    /// </summary>
    public interface ISceneManager
    {
        /// <summary>Create a fresh scene. <paramref name="twoD"/> hints a 2D setup
        /// (orthographic camera, etc.). If <paramref name="savePath"/> is given, the new
        /// scene is saved there (engine asset path).</summary>
        SceneResult NewScene(bool twoD, string? savePath);

        /// <summary>Open an existing scene by engine asset path.</summary>
        SceneResult OpenScene(string path);

        /// <summary>Save the active/edited scene. If <paramref name="path"/> is null,
        /// saves to its current path.</summary>
        SceneResult SaveScene(string? path);
    }

    public class SceneResult
    {
        public string ScenePath { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
