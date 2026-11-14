using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace Yursic.Audio
{
    /// <summary>A loop or one-shot shown in the sound browser.</summary>
    public sealed class SoundItem
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public double Bpm { get; set; }
        public string Source { get; set; } // "Built-in", plugin name, or "USB"

        /// <summary>For built-in synthesized sounds.</summary>
        public Func<int, SampleBuffer> Generate { get; set; }

        /// <summary>For files (plugin sound packs, USB).</summary>
        public StorageFile File { get; set; }

        public string Subtitle => Bpm > 0 ? $"{Category} · {Bpm:0} BPM · {Source}" : $"{Category} · {Source}";

        /// <summary>Puts the sound into the project's audio folder as WAV and returns the stored file.</summary>
        public async Task<StorageFile> MaterializeAsync(StorageFolder audioFolder, int rate)
        {
            if (File != null)
                return await AudioImporter.ImportAsWavAsync(File, audioFolder);

            var buffer = Generate(rate);
            string safe = string.Concat(Name.Split(Path.GetInvalidFileNameChars()));
            var file = await audioFolder.CreateFileAsync(safe + ".wav", CreationCollisionOption.GenerateUniqueName);
            using (var s = await file.OpenStreamForWriteAsync())
                await Task.Run(() => WavFile.Write(s, buffer.Data, rate));
            return file;
        }
    }

    /// <summary>Small synthesized starter library so the app is usable with no USB stick attached.</summary>
    public static class BuiltInSounds
    {
        public static IReadOnlyList<SoundItem> All { get; } = new List<SoundItem>
        {
            Item("Boom Bap Beat", "Drums", 90, sr => Beat(sr, 90, "K...S..K.KS.....", "x.x.x.x.x.x.x.x.")),
            Item("Four on the Floor", "Drums", 120, sr => Beat(sr, 120, "K...K...K...K...", "..x...x...x...x.", "....S.......S...")),
            Item("Trap Hats", "Drums", 140, sr => Beat(sr, 140, "K.......K.K.....", "xxxxxxxxxxxxxxxx", "........S.......")),
            Item("Sub Bass Line", "Bass", 120, sr => Bass(sr, 120, new[] { 36, 36, 43, 41 })),
            Item("Minor Pad", "Keys", 120, sr => Pad(sr, 120, new[] { 57, 60, 64 }, new[] { 53, 57, 60 })),
            Item("Pluck Arp", "Synth", 120, sr => Arp(sr, 120, new[] { 69, 72, 76, 79, 76, 72 })),
        };

        private static SoundItem Item(string name, string cat, double bpm, Func<int, SampleBuffer> gen) =>
            new SoundItem { Name = name, Category = cat, Bpm = bpm, Source = "Built-in", Generate = gen };

        private static double Freq(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

        // Pattern strings: 16 steps per bar, 2 bars (pattern repeats). K=kick, S=snare, x=hat.
        private static SampleBuffer Beat(int sr, double bpm, params string[] patterns)
        {
            double step = 60.0 / bpm / 4;
            int bars = 2;
            long frames = (long)(step * 16 * bars * sr);
            var d = new float[frames * 2];
            var rng = new Random(7);
            for (int bar = 0; bar < bars; bar++)
                foreach (var p in patterns)
                    for (int i = 0; i < 16 && i < p.Length; i++)
                    {
                        long start = (long)((bar * 16 + i) * step * sr);
                        switch (p[i])
                        {
                            case 'K': Kick(d, start, sr); break;
                            case 'S': Snare(d, start, sr, rng); break;
                            case 'x': Hat(d, start, sr, rng, i % 4 == 0 ? 0.35f : 0.22f); break;
                        }
                    }
            return new SampleBuffer(d, sr);
        }

        private static void Add(float[] d, long frame, float v, float pan = 0)
        {
            if (frame < 0 || 2 * frame + 1 >= d.Length) return;
            d[2 * frame] += v * (1 - Math.Max(0, pan));
            d[2 * frame + 1] += v * (1 + Math.Min(0, pan));
        }

        private static void Kick(float[] d, long start, int sr)
        {
            int len = (int)(0.4 * sr);
            double phase = 0;
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / sr;
                double f = 45 + 110 * Math.Exp(-t * 30);
                phase += 2 * Math.PI * f / sr;
                Add(d, start + i, (float)(Math.Sin(phase) * Math.Exp(-t * 7) * 0.9));
            }
        }

        private static void Snare(float[] d, long start, int sr, Random rng)
        {
            int len = (int)(0.25 * sr);
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / sr;
                double noise = (rng.NextDouble() * 2 - 1) * Math.Exp(-t * 18);
                double tone = Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 25);
                Add(d, start + i, (float)((noise * 0.45 + tone * 0.35)));
            }
        }

        private static void Hat(float[] d, long start, int sr, Random rng, float amp)
        {
            int len = (int)(0.05 * sr);
            float prev = 0;
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / sr;
                float n = (float)(rng.NextDouble() * 2 - 1);
                float hp = n - prev; // crude high-pass
                prev = n;
                Add(d, start + i, (float)(hp * amp * Math.Exp(-t * 60)), 0.2f);
            }
        }

        private static SampleBuffer Bass(int sr, double bpm, int[] notes)
        {
            double beat = 60.0 / bpm;
            long frames = (long)(beat * 8 * sr);
            var d = new float[frames * 2];
            for (int n = 0; n < 8; n++)
            {
                double f = Freq(notes[(n / 2) % notes.Length]);
                long start = (long)(n * beat * sr);
                int len = (int)(beat * 0.9 * sr);
                for (int i = 0; i < len; i++)
                {
                    double t = (double)i / sr;
                    double env = Math.Min(1, t * 200) * Math.Exp(-t * 2.5);
                    double s = Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(4 * Math.PI * f * t);
                    Add(d, start + i, (float)(Math.Tanh(s * 1.5) * env * 0.5));
                }
            }
            return new SampleBuffer(d, sr);
        }

        private static SampleBuffer Pad(int sr, double bpm, int[] chordA, int[] chordB)
        {
            double bar = 60.0 / bpm * 4;
            long frames = (long)(bar * 2 * sr);
            var d = new float[frames * 2];
            for (int c = 0; c < 2; c++)
            {
                var chord = c == 0 ? chordA : chordB;
                long start = (long)(c * bar * sr);
                int len = (int)(bar * sr);
                foreach (var note in chord)
                {
                    double f = Freq(note);
                    for (int i = 0; i < len; i++)
                    {
                        double t = (double)i / sr;
                        double env = Math.Min(1, t * 3) * Math.Min(1, (len - i) / (0.3 * sr));
                        double s = Math.Sin(2 * Math.PI * f * t) + 0.5 * Math.Sin(2 * Math.PI * f * 1.003 * t) + 0.25 * Math.Sin(4 * Math.PI * f * t);
                        Add(d, start + i, (float)(s * env * 0.09), (float)Math.Sin(t * 0.7) * 0.3f);
                    }
                }
            }
            return new SampleBuffer(d, sr);
        }

        private static SampleBuffer Arp(int sr, double bpm, int[] notes)
        {
            double step = 60.0 / bpm / 4;
            long frames = (long)(step * 32 * sr);
            var d = new float[frames * 2];
            for (int n = 0; n < 32; n++)
            {
                double f = Freq(notes[n % notes.Length]);
                long start = (long)(n * step * sr);
                int len = (int)(0.3 * sr);
                float pan = n % 2 == 0 ? -0.3f : 0.3f;
                for (int i = 0; i < len; i++)
                {
                    double t = (double)i / sr;
                    double saw = 2 * ((f * t) % 1.0) - 1;
                    double env = Math.Exp(-t * 14);
                    Add(d, start + i, (float)(saw * env * 0.18), pan);
                }
            }
            return new SampleBuffer(d, sr);
        }
    }
}
