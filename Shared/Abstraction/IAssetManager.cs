using System.Collections.Generic;

namespace MCPSharp.Shared.Abstraction
{
    public interface IAssetManager
    {
        IEnumerable<AssetInfo> Search(string query, string? typeFilter = null);
        object? LoadAsset(string path);
        void SaveAsset(object asset, string path);
        bool DeleteAsset(string path);
        string? GetAssetPath(object asset);
    }
}
