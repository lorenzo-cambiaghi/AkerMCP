namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this provide high-quality
    /// internal render-buffer capture. Engines that don't implement it
    /// fall back to OS-level screen capture on the Server side.
    /// </summary>
    public interface IScreenCapture
    {
        /// <param name="viewType">"game" or "scene"</param>
        /// <returns>Encoded image bytes plus MIME type (e.g. "image/png"), or null if unsupported.</returns>
        (byte[] bytes, string contentType)? CaptureView(string viewType);
    }
}
