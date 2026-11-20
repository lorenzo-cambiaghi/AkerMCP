#if TOOLS
#nullable enable
using System.Collections.Generic;
using Godot;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Makes the editor import a file the plugin has just written under res://, synchronously,
    /// and loads the imported resource. A resource loaded from its own res:// path is saved in a
    /// scene as an ext_resource reference. The importers used to build an ImageTexture or an
    /// AudioStreamWav straight from the bytes instead, and the scene then carried a full copy of
    /// the data: a 10 s placeholder sound put about 880 KB of PCM inside the .tscn.
    /// </summary>
    internal static class GodotResourceImport
    {
        /// <summary>
        /// Imports <paramref name="resPath"/> and returns it loaded, or null when the editor cannot
        /// import it right now (a full filesystem scan is running, or the import failed). On null
        /// the file is still on disk and the editor imports it on its next scan.
        /// </summary>
        /// <param name="importParams">Importer options to set before importing (the [params] of the
        /// .import file), or null to keep the importer's defaults.</param>
        public static T? ImportAndLoad<T>(string resPath, IReadOnlyDictionary<string, Variant>? importParams = null)
            where T : Resource
        {
            var fs = EditorInterface.Singleton.GetResourceFilesystem();
            // During a full scan the filesystem cannot find the file, and ReimportFiles fails with
            // "Can't find file during file reimport".
            if (fs == null || fs.IsScanning()) return null;

            if (importParams != null)
                WriteImportParams(resPath, importParams);

            // UpdateFile tells the filesystem the file exists but does not import it; ReimportFiles
            // runs the importer now, on this thread. Scan() would import too, but later, on its own
            // schedule: that is why the old code could not use the file it had just written.
            fs.UpdateFile(resPath);
            fs.ReimportFiles(new[] { resPath });

            if (!ResourceLoader.Exists(resPath)) return null;

            // Replace, not Reuse: a second import under the same name must show the new content,
            // and Replace updates the cached instance that scenes already hold.
            return ResourceLoader.Load<T>(resPath, null, ResourceLoader.CacheMode.Replace);
        }

        // The importer reads its options from the .import file next to the asset and keeps the rest
        // of that file (the uid above all) when it rewrites it, so options are merged into an
        // existing file instead of replacing it.
        private static void WriteImportParams(string resPath, IReadOnlyDictionary<string, Variant> importParams)
        {
            var importFile = resPath + ".import";
            var cfg = new ConfigFile();
            if (Godot.FileAccess.FileExists(importFile))
                cfg.Load(importFile);
            foreach (var kv in importParams)
                cfg.SetValue("params", kv.Key, kv.Value);
            cfg.Save(importFile);
        }
    }
}
#endif
