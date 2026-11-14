using System;
using System.Collections.Generic;

namespace Yursic.Audio
{
    /// <summary>Decoded audio, always stereo interleaved float at a known sample rate.</summary>
    public sealed class SampleBuffer
    {
        public SampleBuffer(float[] data, int sampleRate)
        {
            Data = data;
            SampleRate = sampleRate;
        }

        public float[] Data { get; }
        public int SampleRate { get; }
        public long Frames => Data.Length / 2;
        public double Seconds => (double)Frames / SampleRate;

        private float[] _peaks;
        public const int PeakResolution = 512; // frames per peak bucket at the buffer's rate

        /// <summary>Max |sample| per bucket, used for drawing waveforms.</summary>
        public float[] Peaks
        {
            get
            {
                if (_peaks != null) return _peaks;
                long buckets = (Frames + PeakResolution - 1) / PeakResolution;
                var p = new float[buckets];
                for (long b = 0; b < buckets; b++)
                {
                    long start = b * PeakResolution, end = Math.Min(Frames, start + PeakResolution);
                    float m = 0;
                    for (long i = start; i < end; i++)
                    {
                        float l = Math.Abs(Data[2 * i]), r = Math.Abs(Data[2 * i + 1]);
                        if (l > m) m = l;
                        if (r > m) m = r;
                    }
                    p[b] = m;
                }
                _peaks = p;
                return p;
            }
        }

        /// <summary>Linear-interpolation resample to a new rate (used when a file's rate differs from the device).</summary>
        public SampleBuffer ResampleTo(int targetRate)
        {
            if (targetRate == SampleRate || Frames == 0) return this;
            double ratio = (double)SampleRate / targetRate;
            long outFrames = (long)(Frames / ratio);
            var o = new float[outFrames * 2];
            for (long i = 0; i < outFrames; i++)
            {
                double src = i * ratio;
                long i0 = (long)src;
                long i1 = Math.Min(i0 + 1, Frames - 1);
                float t = (float)(src - i0);
                o[2 * i] = Data[2 * i0] + (Data[2 * i1] - Data[2 * i0]) * t;
                o[2 * i + 1] = Data[2 * i0 + 1] + (Data[2 * i1 + 1] - Data[2 * i0 + 1]) * t;
            }
            return new SampleBuffer(o, targetRate);
        }
    }

    /// <summary>Keeps decoded files in memory so clips that share a file share the samples.</summary>
    public static class SampleCache
    {
        private static readonly Dictionary<string, SampleBuffer> Cache = new Dictionary<string, SampleBuffer>(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string key, out SampleBuffer buffer)
        {
            lock (Cache) return Cache.TryGetValue(key, out buffer);
        }

        public static void Put(string key, SampleBuffer buffer)
        {
            lock (Cache) Cache[key] = buffer;
        }

        public static void Clear()
        {
            lock (Cache) Cache.Clear();
        }
    }
}
