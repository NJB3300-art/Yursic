using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Yursic.Audio;
using Yursic.Plugins;

namespace Yursic.Models
{
    public sealed class ProjectSummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double Bpm { get; set; }
        public int TrackCount { get; set; }
        public DateTimeOffset Modified { get; set; }
        public string Subtitle => $"{TrackCount} track{(TrackCount == 1 ? "" : "s")} · {Bpm:0} BPM · {Helpers.Format.Relative(Modified)}";
    }

    /// <summary>Projects live in LocalFolder\Projects\{id}\project.json with WAVs in \audio.</summary>
    public sealed class ProjectStore
    {
        public const string ProjectFileName = "project.json";
        public const string AudioFolderName = "audio";
        public const string ArchiveExtension = ".yproj";

        public Project Current { get; set; }

        public static Task<StorageFolder> GetRootAsync() =>
            ApplicationData.Current.LocalFolder.CreateFolderAsync("Projects", CreationCollisionOption.OpenIfExists).AsTask();

        public async Task<List<ProjectSummary>> ListAsync()
        {
            var list = new List<ProjectSummary>();
            var root = await GetRootAsync();
            foreach (var folder in await root.GetFoldersAsync())
            {
                try
                {
                    var pf = await ReadFileAsync(folder);
                    list.Add(new ProjectSummary
                    {
                        Id = folder.Name,
                        Name = pf.Name,
                        Bpm = pf.Bpm,
                        TrackCount = pf.Tracks?.Count ?? 0,
                        Modified = ParseDate(pf.Modified)
                    });
                }
                catch { /* skip broken folders */ }
            }
            return list.OrderByDescending(p => p.Modified).ToList();
        }

        public async Task<Project> CreateAsync(string name)
        {
            var p = new Project { Name = name };
            p.AddTrack("Vocals");
            p.AddTrack("Beat");
            await EnsureFoldersAsync(p);
            await SaveAsync(p);
            return p;
        }

        private static async Task EnsureFoldersAsync(Project p)
        {
            var root = await GetRootAsync();
            p.Folder = await root.CreateFolderAsync(p.Id, CreationCollisionOption.OpenIfExists);
            p.AudioFolder = await p.Folder.CreateFolderAsync(AudioFolderName, CreationCollisionOption.OpenIfExists);
        }

        public async Task SaveAsync(Project p)
        {
            if (p.Folder == null) await EnsureFoldersAsync(p);
            p.Modified = DateTimeOffset.Now;
            var pf = new ProjectFile
            {
                Id = p.Id,
                Name = p.Name,
                Bpm = p.Bpm,
                MasterVolume = p.MasterVolume,
                Created = p.Created.ToString("o"),
                Modified = p.Modified.ToString("o"),
                Tracks = p.Tracks.Select(t => new TrackFile
                {
                    Id = t.Id,
                    Name = t.Name,
                    Color = t.ColorHex,
                    Volume = t.Volume,
                    Pan = t.Pan,
                    Mute = t.Mute,
                    Solo = t.Solo,
                    Clips = t.Clips.Select(c => new ClipFile
                    {
                        Id = c.Id,
                        File = c.FileName,
                        Name = c.Name,
                        Start = c.StartSeconds,
                        Offset = c.OffsetSeconds,
                        Length = c.LengthSeconds,
                        Gain = c.Gain
                    }).ToList(),
                    Effects = t.Effects.Select(e => new EffectFile
                    {
                        PluginId = e.Definition.Id,
                        Enabled = e.Enabled,
                        Values = e.Values.Select(kv => new ParamValueFile { Id = kv.Key, Value = kv.Value }).ToList()
                    }).ToList()
                }).ToList()
            };

            var ser = new DataContractJsonSerializer(typeof(ProjectFile));
            var file = await p.Folder.CreateFileAsync(ProjectFileName, CreationCollisionOption.ReplaceExisting);
            using (var s = await file.OpenStreamForWriteAsync())
            {
                s.SetLength(0);
                ser.WriteObject(s, pf);
            }
        }

        /// <summary>Loads a project and decodes its audio at the engine's sample rate.</summary>
        public async Task<Project> LoadAsync(string id, int sampleRate, PluginManager plugins, List<string> warnings)
        {
            var root = await GetRootAsync();
            var folder = await root.GetFolderAsync(id);
            var pf = await ReadFileAsync(folder);
            var p = new Project
            {
                Id = id,
                Name = pf.Name ?? "Untitled",
                Bpm = pf.Bpm > 0 ? pf.Bpm : 120,
                MasterVolume = pf.MasterVolume > 0 ? pf.MasterVolume : 0.9,
                Created = ParseDate(pf.Created),
                Modified = ParseDate(pf.Modified),
                Folder = folder,
                AudioFolder = await folder.CreateFolderAsync(AudioFolderName, CreationCollisionOption.OpenIfExists)
            };

            foreach (var tf in pf.Tracks ?? new List<TrackFile>())
            {
                var t = new Track
                {
                    Id = tf.Id ?? Guid.NewGuid().ToString("N"),
                    Name = tf.Name,
                    ColorHex = tf.Color ?? Project.TrackColors[p.Tracks.Count % Project.TrackColors.Length],
                    Volume = tf.Volume,
                    Pan = tf.Pan,
                    Mute = tf.Mute,
                    Solo = tf.Solo
                };
                foreach (var cf in tf.Clips ?? new List<ClipFile>())
                {
                    try
                    {
                        var wav = await p.AudioFolder.GetFileAsync(cf.File);
                        var buf = await AudioImporter.LoadAsync(wav, sampleRate);
                        t.Clips.Add(new Clip
                        {
                            Id = cf.Id ?? Guid.NewGuid().ToString("N"),
                            FileName = cf.File,
                            Name = cf.Name,
                            Buffer = buf,
                            StartSeconds = cf.Start,
                            OffsetSeconds = cf.Offset,
                            LengthSeconds = cf.Length > 0 ? cf.Length : buf.Seconds,
                            Gain = cf.Gain > 0 ? cf.Gain : 1
                        });
                    }
                    catch (Exception ex)
                    {
                        warnings?.Add($"Missing audio \"{cf.File}\" on {t.Name}: {ex.Message}");
                    }
                }
                foreach (var ef in tf.Effects ?? new List<EffectFile>())
                {
                    var def = plugins.FindEffect(ef.PluginId);
                    if (def == null)
                    {
                        warnings?.Add($"Plugin \"{ef.PluginId}\" isn't installed, so it was skipped on {t.Name}.");
                        continue;
                    }
                    var inst = new EffectInstance(def) { Enabled = ef.Enabled };
                    foreach (var v in ef.Values ?? new List<ParamValueFile>())
                        if (def.Params.Any(x => x.Id == v.Id)) inst.SetValue(v.Id, v.Value);
                    t.Effects.Add(inst);
                }
                p.Tracks.Add(t);
            }
            return p;
        }

        public async Task DeleteAsync(string id)
        {
            var root = await GetRootAsync();
            var item = await root.TryGetItemAsync(id);
            if (item != null) await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }

        public async Task RenameAsync(string id, string newName)
        {
            var root = await GetRootAsync();
            var folder = await root.GetFolderAsync(id);
            var pf = await ReadFileAsync(folder);
            pf.Name = newName;
            var ser = new DataContractJsonSerializer(typeof(ProjectFile));
            var file = await folder.CreateFileAsync(ProjectFileName, CreationCollisionOption.ReplaceExisting);
            using (var s = await file.OpenStreamForWriteAsync())
                ser.WriteObject(s, pf);
        }

        /// <summary>Writes the whole project (json + audio) as a single .yproj zip into <paramref name="dest"/>.</summary>
        public async Task<StorageFile> ExportArchiveAsync(string id, StorageFolder dest)
        {
            var root = await GetRootAsync();
            var folder = await root.GetFolderAsync(id);
            var pf = await ReadFileAsync(folder);
            string safe = string.Concat((pf.Name ?? "Project").Split(Path.GetInvalidFileNameChars()));
            var outFile = await dest.CreateFileAsync(safe + ArchiveExtension, CreationCollisionOption.GenerateUniqueName);

            using (var os = await outFile.OpenStreamForWriteAsync())
            using (var zip = new ZipArchive(os, ZipArchiveMode.Create))
            {
                await AddFileAsync(zip, await folder.GetFileAsync(ProjectFileName), ProjectFileName);
                var audio = await folder.TryGetItemAsync(AudioFolderName) as StorageFolder;
                if (audio != null)
                    foreach (var f in await audio.GetFilesAsync())
                        await AddFileAsync(zip, f, AudioFolderName + "/" + f.Name);
            }
            return outFile;
        }

        private static async Task AddFileAsync(ZipArchive zip, StorageFile file, string entryName)
        {
            var entry = zip.CreateEntry(entryName, entryName.EndsWith(".json") ? CompressionLevel.Optimal : CompressionLevel.Fastest);
            using (var es = entry.Open())
            using (var fs = await file.OpenStreamForReadAsync())
                await fs.CopyToAsync(es);
        }

        /// <summary>Imports a .yproj. Always gets a fresh id so it never overwrites a local project.</summary>
        public async Task<string> ImportArchiveAsync(StorageFile archive)
        {
            var root = await GetRootAsync();
            string id = Guid.NewGuid().ToString("N");
            var folder = await root.CreateFolderAsync(id, CreationCollisionOption.FailIfExists);
            try
            {
                using (var src = await archive.OpenStreamForReadAsync())
                using (var ms = new MemoryStream())
                {
                    await src.CopyToAsync(ms);
                    ms.Position = 0;
                    using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                    {
                        if (zip.GetEntry(ProjectFileName) == null)
                            throw new InvalidDataException("This file has no project.json.");
                        var audio = await folder.CreateFolderAsync(AudioFolderName, CreationCollisionOption.OpenIfExists);
                        foreach (var e in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(e.Name)) continue;
                            StorageFolder target;
                            if (e.FullName == ProjectFileName) target = folder;
                            else if (e.FullName == AudioFolderName + "/" + e.Name) target = audio;
                            else continue; // ignore anything unexpected
                            var f = await target.CreateFileAsync(e.Name, CreationCollisionOption.ReplaceExisting);
                            using (var es = e.Open())
                            using (var fs = await f.OpenStreamForWriteAsync())
                                await es.CopyToAsync(fs);
                        }
                    }
                }
                // Give it the new id and mark it as imported.
                var pf = await ReadFileAsync(folder);
                pf.Id = id;
                var ser = new DataContractJsonSerializer(typeof(ProjectFile));
                var file = await folder.CreateFileAsync(ProjectFileName, CreationCollisionOption.ReplaceExisting);
                using (var s = await file.OpenStreamForWriteAsync())
                    ser.WriteObject(s, pf);
                return id;
            }
            catch
            {
                await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                throw;
            }
        }

        private static async Task<ProjectFile> ReadFileAsync(StorageFolder folder)
        {
            var file = await folder.GetFileAsync(ProjectFileName);
            var ser = new DataContractJsonSerializer(typeof(ProjectFile));
            using (var s = await file.OpenStreamForReadAsync())
                return (ProjectFile)ser.ReadObject(s);
        }

        private static DateTimeOffset ParseDate(string s) =>
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.Now;
    }
}
