using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Yursic.Audio;

namespace Yursic.Plugins
{
    /// <summary>Installs, lists and removes plugins. Installed plugins live in LocalFolder\Plugins\{id}.</summary>
    public sealed class PluginManager
    {
        public const string ManifestName = "plugin.json";
        private const long MaxPluginBytes = 512L * 1024 * 1024;

        public ObservableCollection<PluginDefinition> Installed { get; } = new ObservableCollection<PluginDefinition>();
        public List<string> LoadErrors { get; } = new List<string>();

        public IEnumerable<PluginDefinition> AllEffects =>
            BuiltInEffects.All.Concat(Installed.Where(p => p.IsEffect));

        public PluginDefinition FindEffect(string id) =>
            AllEffects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        public static Task<StorageFolder> GetRootAsync() =>
            ApplicationData.Current.LocalFolder.CreateFolderAsync("Plugins", CreationCollisionOption.OpenIfExists).AsTask();

        public async Task LoadInstalledAsync()
        {
            Installed.Clear();
            LoadErrors.Clear();
            var root = await GetRootAsync();
            foreach (var folder in await root.GetFoldersAsync())
            {
                try
                {
                    var file = await folder.GetFileAsync(ManifestName);
                    var def = PluginDefinition.Parse(await FileIO.ReadTextAsync(file));
                    def.Folder = folder;
                    Installed.Add(def);
                }
                catch (Exception ex)
                {
                    LoadErrors.Add($"{folder.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Installs from a .yplug / .zip archive, or a plugin.json (whose folder is copied).</summary>
        public async Task<PluginDefinition> InstallAsync(IStorageItem item)
        {
            if (item is StorageFolder folder)
                return await InstallFromFolderAsync(folder);

            var file = (StorageFile)item;
            if (string.Equals(file.Name, ManifestName, StringComparison.OrdinalIgnoreCase))
                return await InstallFromFolderAsync(await file.GetParentAsync()
                    ?? throw new InvalidOperationException("Can't open the folder that holds plugin.json."));

            var props = await file.GetBasicPropertiesAsync();
            if (props.Size > (ulong)MaxPluginBytes)
                throw new InvalidOperationException("Plugin is larger than 512 MB.");

            using (var src = await file.OpenStreamForReadAsync())
            using (var ms = new MemoryStream())
            {
                await src.CopyToAsync(ms);
                ms.Position = 0;
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    var manifest = zip.Entries
                        .Where(e => string.Equals(e.Name, ManifestName, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName.Length)
                        .FirstOrDefault();
                    if (manifest == null)
                        throw new InvalidDataException($"{file.Name} has no {ManifestName} inside.");

                    string prefix = manifest.FullName.Substring(0, manifest.FullName.Length - manifest.Name.Length);
                    string json;
                    using (var r = new StreamReader(manifest.Open()))
                        json = await r.ReadToEndAsync();
                    var def = PluginDefinition.Parse(json);

                    var target = await PrepareTargetAsync(def.Id);
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // directory
                        if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        string rel = entry.FullName.Substring(prefix.Length).Replace('\\', '/');
                        if (rel.Contains("..") || rel.StartsWith("/") || rel.Contains(":"))
                            throw new InvalidDataException($"Unsafe path in archive: {entry.FullName}");

                        var parts = rel.Split('/');
                        var dir = target;
                        for (int i = 0; i < parts.Length - 1; i++)
                            dir = await dir.CreateFolderAsync(parts[i], CreationCollisionOption.OpenIfExists);
                        var outFile = await dir.CreateFileAsync(parts[parts.Length - 1], CreationCollisionOption.ReplaceExisting);
                        using (var es = entry.Open())
                        using (var os = await outFile.OpenStreamForWriteAsync())
                            await es.CopyToAsync(os);
                    }
                    return Register(def, target);
                }
            }
        }

        private async Task<PluginDefinition> InstallFromFolderAsync(StorageFolder source)
        {
            var manifest = await source.GetFileAsync(ManifestName);
            var def = PluginDefinition.Parse(await FileIO.ReadTextAsync(manifest));
            var target = await PrepareTargetAsync(def.Id);
            await CopyFolderAsync(source, target);
            return Register(def, target);
        }

        private async Task<StorageFolder> PrepareTargetAsync(string id)
        {
            var root = await GetRootAsync();
            var existing = await root.TryGetItemAsync(id);
            if (existing != null) await existing.DeleteAsync(StorageDeleteOption.PermanentDelete);
            return await root.CreateFolderAsync(id, CreationCollisionOption.ReplaceExisting);
        }

        private PluginDefinition Register(PluginDefinition def, StorageFolder folder)
        {
            def.Folder = folder;
            var old = Installed.FirstOrDefault(p => p.Id == def.Id);
            if (old != null) Installed.Remove(old);
            Installed.Add(def);
            return def;
        }

        public async Task UninstallAsync(PluginDefinition def)
        {
            if (def.IsBuiltIn) return;
            if (def.Folder != null) await def.Folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            Installed.Remove(def);
        }

        public static async Task CopyFolderAsync(StorageFolder source, StorageFolder dest)
        {
            foreach (var f in await source.GetFilesAsync())
                await f.CopyAsync(dest, f.Name, NameCollisionOption.ReplaceExisting);
            foreach (var sub in await source.GetFoldersAsync())
            {
                var d = await dest.CreateFolderAsync(sub.Name, CreationCollisionOption.OpenIfExists);
                await CopyFolderAsync(sub, d);
            }
        }

        /// <summary>Sounds from installed sound-pack plugins.</summary>
        public async Task<List<SoundItem>> GetPluginSoundsAsync()
        {
            var list = new List<SoundItem>();
            foreach (var p in Installed.Where(x => x.HasSounds && x.Folder != null))
                foreach (var s in p.Sounds)
                {
                    var item = await p.Folder.TryGetItemAsync(s.File.Replace('/', '\\'));
                    if (item is StorageFile file)
                        list.Add(new SoundItem { Name = s.Name, Category = s.Category, Bpm = s.Bpm, Source = p.Name, File = file });
                }
            return list;
        }
    }
}
