using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Yursic.Audio
{
    public static class AudioImporter
    {
        public static readonly string[] SupportedExtensions = { ".wav", ".mp3", ".m4a", ".wma", ".flac", ".aac" };

        public static bool IsSupported(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return Array.IndexOf(SupportedExtensions, ext) >= 0;
        }

        /// <summary>
        /// Copies (WAV) or transcodes (everything else) a file into <paramref name="destFolder"/> as WAV.
        /// Returns the stored WAV file.
        /// </summary>
        public static async Task<StorageFile> ImportAsWavAsync(IStorageFile source, StorageFolder destFolder)
        {
            string baseName = Path.GetFileNameWithoutExtension(source.Name);
            if (string.Equals(Path.GetExtension(source.Name), ".wav", StringComparison.OrdinalIgnoreCase))
                return await source.CopyAsync(destFolder, baseName + ".wav", NameCollisionOption.GenerateUniqueName);

            var dest = await destFolder.CreateFileAsync(baseName + ".wav", CreationCollisionOption.GenerateUniqueName);
            var transcoder = new MediaTranscoder();
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, dest, profile);
            if (!prepared.CanTranscode)
            {
                await dest.DeleteAsync();
                throw new InvalidOperationException($"Can't convert {source.Name}: {prepared.FailureReason}");
            }
            await prepared.TranscodeAsync();
            return dest;
        }

        /// <summary>Loads a WAV into memory at the engine's sample rate, using the shared cache.</summary>
        public static async Task<SampleBuffer> LoadAsync(StorageFile wav, int targetRate)
        {
            string key = wav.Path + "@" + targetRate;
            if (SampleCache.TryGet(key, out var cached)) return cached;

            SampleBuffer buffer;
            using (var s = await wav.OpenStreamForReadAsync())
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms);
                ms.Position = 0;
                buffer = await Task.Run(() => WavFile.Read(ms).ResampleTo(targetRate));
            }
            SampleCache.Put(key, buffer);
            return buffer;
        }

        public static async Task SaveWavAsync(StorageFolder folder, string name, float[] stereo, int rate)
        {
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            using (var s = await file.OpenStreamForWriteAsync())
            {
                s.SetLength(0);
                await Task.Run(() => WavFile.Write(s, stereo, rate));
            }
        }
    }
}
