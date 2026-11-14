using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Yursic.Audio;
using Yursic.Helpers;
using Yursic.Plugins;

namespace Yursic.Storage
{
    public sealed class UsbDrive
    {
        public StorageFolder Root { get; set; }
        public string Name { get; set; }
        public ulong Capacity { get; set; }
        public ulong Free { get; set; }
        public ulong Used => Capacity > Free ? Capacity - Free : 0;
        public double UsedPercent => Capacity == 0 ? 0 : 100.0 * Used / Capacity;
        public string SpaceText => Capacity == 0
            ? "Size unknown"
            : $"{Format.Bytes(Free)} free of {Format.Bytes(Capacity)}";
    }

    public sealed class StorageEntry
    {
        public IStorageItem Item { get; set; }
        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public ulong Size { get; set; }
        public string Kind { get; set; }
        public string Location { get; set; }

        public string Glyph
        {
            get
            {
                if (IsFolder) return "";
                switch (Kind)
                {
                    case "Audio": return "";
                    case "Plugin": return "";
                    case "Project": return "";
                    default: return "";
                }
            }
        }

        public string Detail => IsFolder
            ? (string.IsNullOrEmpty(Location) ? "Folder" : $"Folder · {Location}")
            : string.IsNullOrEmpty(Location) ? $"{Kind} · {Format.Bytes(Size)}" : $"{Kind} · {Format.Bytes(Size)} · {Location}";
    }

    /// <summary>Everything that touches USB drives. Requires the removableStorage capability.</summary>
    public sealed class UsbStorageService
    {
        public const string AppFolder = "Yursic";
        public static readonly string[] SubFolders = { "Plugins", "Projects", "Samples", "Exports" };

        public async Task<List<UsbDrive>> GetDrivesAsync()
        {
            var drives = new List<UsbDrive>();
            IReadOnlyList<StorageFolder> roots;
            try { roots = await KnownFolders.RemovableDevices.GetFoldersAsync(); }
            catch { return drives; }

            foreach (var root in roots)
            {
                var d = new UsbDrive { Root = root, Name = string.IsNullOrEmpty(root.DisplayName) ? root.Name : root.DisplayName };
                try
                {
                    var props = await root.Properties.RetrievePropertiesAsync(new[] { "System.Capacity", "System.FreeSpace" });
                    if (props.TryGetValue("System.Capacity", out var cap) && cap != null) d.Capacity = Convert.ToUInt64(cap);
                    if (props.TryGetValue("System.FreeSpace", out var free) && free != null) d.Free = Convert.ToUInt64(free);
                }
                catch { /* some drives don't report size */ }
                drives.Add(d);
            }
            return drives;
        }

        /// <summary>Creates Yursic\Plugins, \Projects, \Samples and \Exports on the drive.</summary>
        public async Task<StorageFolder> PrepareDriveAsync(UsbDrive drive)
        {
            var app = await drive.Root.CreateFolderAsync(AppFolder, CreationCollisionOption.OpenIfExists);
            foreach (var s in SubFolders)
                await app.CreateFolderAsync(s, CreationCollisionOption.OpenIfExists);
            return app;
        }

        public async Task<StorageFolder> GetSubFolderAsync(UsbDrive drive, string sub)
        {
            var app = await PrepareDriveAsync(drive);
            return await app.GetFolderAsync(sub);
        }

        private static async Task<StorageFolder> TryGetFolderAsync(StorageFolder root, string relative)
        {
            try { return await root.TryGetItemAsync(relative) as StorageFolder; }
            catch { return null; }
        }

        /// <summary>Plugin archives (.yplug/.zip) and plugin folders in the drive root and Yursic\Plugins.</summary>
        public async Task<List<StorageEntry>> FindPluginsAsync()
        {
            var list = new List<StorageEntry>();
            foreach (var drive in await GetDrivesAsync())
            {
                var places = new List<StorageFolder> { drive.Root };
                var pf = await TryGetFolderAsync(drive.Root, AppFolder + "\\Plugins");
                if (pf != null) places.Add(pf);

                foreach (var place in places)
                {
                    try
                    {
                        foreach (var f in await place.GetFilesAsync())
                        {
                            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
                            if (ext == ".yplug" || ext == ".zip")
                                list.Add(await EntryAsync(f, "Plugin", drive.Name));
                        }
                        foreach (var sub in await place.GetFoldersAsync())
                        {
                            if (await sub.TryGetItemAsync(PluginManager.ManifestName) is StorageFile)
                                list.Add(new StorageEntry { Item = sub, Name = sub.Name, IsFolder = true, Kind = "Plugin", Location = drive.Name });
                        }
                    }
                    catch { /* unreadable folder */ }
                }
            }
            return list;
        }

        /// <summary>Audio files in Yursic\Samples (searched a few levels deep) and the drive root.</summary>
        public async Task<List<StorageEntry>> FindAudioAsync(int max = 400)
        {
            var list = new List<StorageEntry>();
            foreach (var drive in await GetDrivesAsync())
            {
                await CollectAudioAsync(drive.Root, drive.Name, list, 0, 0, max);
                var samples = await TryGetFolderAsync(drive.Root, AppFolder + "\\Samples");
                if (samples != null) await CollectAudioAsync(samples, drive.Name, list, 0, 3, max);
            }
            return list;
        }

        private async Task CollectAudioAsync(StorageFolder folder, string drive, List<StorageEntry> list, int depth, int maxDepth, int max)
        {
            if (list.Count >= max) return;
            try
            {
                foreach (var f in await folder.GetFilesAsync())
                {
                    if (list.Count >= max) return;
                    if (AudioImporter.IsSupported(f.Name))
                        list.Add(await EntryAsync(f, "Audio", depth == 0 ? drive : $"{drive} › {folder.Name}"));
                }
                if (depth < maxDepth)
                    foreach (var sub in await folder.GetFoldersAsync())
                        await CollectAudioAsync(sub, drive, list, depth + 1, maxDepth, max);
            }
            catch { /* skip */ }
        }

        public async Task<List<StorageEntry>> FindProjectArchivesAsync()
        {
            var list = new List<StorageEntry>();
            foreach (var drive in await GetDrivesAsync())
            {
                var places = new List<StorageFolder> { drive.Root };
                var pf = await TryGetFolderAsync(drive.Root, AppFolder + "\\Projects");
                if (pf != null) places.Add(pf);
                foreach (var place in places)
                {
                    try
                    {
                        foreach (var f in await place.GetFilesAsync())
                            if (f.Name.EndsWith(".yproj", StringComparison.OrdinalIgnoreCase))
                                list.Add(await EntryAsync(f, "Project", drive.Name));
                    }
                    catch { }
                }
            }
            return list;
        }

        /// <summary>Folder listing for the file browser.</summary>
        public async Task<List<StorageEntry>> ListAsync(StorageFolder folder)
        {
            var list = new List<StorageEntry>();
            foreach (var sub in await folder.GetFoldersAsync())
                list.Add(new StorageEntry { Item = sub, Name = sub.Name, IsFolder = true, Kind = "Folder" });
            foreach (var f in await folder.GetFilesAsync())
                list.Add(await EntryAsync(f, KindOf(f.Name), null));
            return list;
        }

        public static string KindOf(string name)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (AudioImporter.IsSupported(name)) return "Audio";
            if (ext == ".yplug" || ext == ".zip" || name.Equals(PluginManager.ManifestName, StringComparison.OrdinalIgnoreCase)) return "Plugin";
            if (ext == ".yproj") return "Project";
            return "File";
        }

        private static async Task<StorageEntry> EntryAsync(StorageFile f, string kind, string location)
        {
            ulong size = 0;
            try { size = (await f.GetBasicPropertiesAsync()).Size; } catch { }
            return new StorageEntry { Item = f, Name = f.Name, Size = size, Kind = kind, Location = location };
        }

        public static async Task<ulong> GetFolderSizeAsync(StorageFolder folder)
        {
            ulong total = 0;
            try
            {
                foreach (var f in await folder.GetFilesAsync())
                {
                    BasicProperties p = await f.GetBasicPropertiesAsync();
                    total += p.Size;
                }
                foreach (var sub in await folder.GetFoldersAsync())
                    total += await GetFolderSizeAsync(sub);
            }
            catch { }
            return total;
        }

        public async Task DeleteAsync(IStorageItem item) =>
            await item.DeleteAsync(StorageDeleteOption.PermanentDelete);

        /// <summary>Copies a local file onto the drive (e.g. a mixdown into Yursic\Exports).</summary>
        public async Task<StorageFile> CopyToDriveAsync(StorageFile file, UsbDrive drive, string sub)
        {
            var dest = await GetSubFolderAsync(drive, sub);
            return await file.CopyAsync(dest, file.Name, NameCollisionOption.GenerateUniqueName);
        }
    }
}
