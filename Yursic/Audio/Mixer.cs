using System;
using System.Linq;
using Yursic.Models;
using Yursic.Plugins;

namespace Yursic.Audio
{
    /// <summary>
    /// Renders the project into a stereo buffer. Runs on the audio thread, so it works from an
    /// immutable snapshot of the track/clip/effect lists; the UI rebuilds the snapshot after edits.
    /// Simple values (volume, clip position, mute...) are read live from the model objects.
    /// </summary>
    public sealed class Mixer
    {
        private sealed class TrackSnap
        {
            public Track Track;
            public Clip[] Clips;
            public EffectInstance[] Effects;
        }

        private volatile TrackSnap[] _tracks = new TrackSnap[0];
        private volatile Project _project;
        private float[] _trackBuf = new float[0];

        public bool MetronomeOn { get; set; }
        public float MetronomeLevel { get; set; } = 0.5f;
        public volatile float MasterMeter;

        public void UpdateSnapshot(Project project)
        {
            _project = project;
            if (project == null) { _tracks = new TrackSnap[0]; return; }
            _tracks = project.Tracks.Select(t => new TrackSnap
            {
                Track = t,
                Clips = t.Clips.ToArray(),
                Effects = t.Effects.ToArray()
            }).ToArray();
        }

        public void ResetEffects()
        {
            foreach (var t in _tracks)
                foreach (var e in t.Effects)
                    e.Reset();
        }

        /// <summary>Mixes <paramref name="frames"/> frames starting at timeline frame <paramref name="start"/>.</summary>
        public void Render(float[] output, int frames, long start, int sampleRate, bool metronome)
        {
            int n = frames * 2;
            Array.Clear(output, 0, n);
            if (_trackBuf.Length < n) _trackBuf = new float[n];
            var buf = _trackBuf;

            var tracks = _tracks;
            bool anySolo = false;
            foreach (var t in tracks) if (t.Track.Solo) { anySolo = true; break; }

            long end = start + frames;
            foreach (var ts in tracks)
            {
                var track = ts.Track;
                if (track.Mute || (anySolo && !track.Solo)) { track.MeterLevel = 0; continue; }

                Array.Clear(buf, 0, n);
                bool any = false;
                foreach (var clip in ts.Clips)
                {
                    var data = clip.Buffer;
                    if (data == null) continue;
                    long cs = (long)(clip.StartSeconds * sampleRate);
                    long cl = (long)(clip.LengthSeconds * sampleRate);
                    long ce = cs + cl;
                    if (ce <= start || cs >= end) continue;
                    long off = (long)(clip.OffsetSeconds * sampleRate);
                    long from = Math.Max(start, cs), to = Math.Min(end, ce);
                    float g = (float)clip.Gain;
                    long srcFrames = data.Frames;
                    var src = data.Data;

                    // short fades at clip edges to avoid clicks
                    long fade = Math.Min(sampleRate / 500, cl / 4);
                    for (long pos = from; pos < to; pos++)
                    {
                        long s = off + (pos - cs);
                        if (s < 0 || s >= srcFrames) continue;
                        float env = g;
                        long intoClip = pos - cs, toEnd = ce - pos;
                        if (fade > 0)
                        {
                            if (intoClip < fade) env *= (float)intoClip / fade;
                            else if (toEnd < fade) env *= (float)toEnd / fade;
                        }
                        int o = (int)(pos - start) * 2;
                        buf[o] += src[2 * s] * env;
                        buf[o + 1] += src[2 * s + 1] * env;
                    }
                    any = true;
                }

                // Run effects even with no clip so reverb/delay tails ring out.
                foreach (var fx in ts.Effects)
                    fx.Process(buf, frames, sampleRate);

                double pan = Math.Max(-1, Math.Min(1, track.Pan));
                double angle = (pan + 1) * Math.PI / 4;
                float vol = (float)track.Volume;
                float gl = vol * (float)Math.Cos(angle) * 1.4142f;
                float gr = vol * (float)Math.Sin(angle) * 1.4142f;
                float peak = 0;
                for (int i = 0; i < frames; i++)
                {
                    float l = buf[2 * i] * gl, r = buf[2 * i + 1] * gr;
                    output[2 * i] += l;
                    output[2 * i + 1] += r;
                    float a = Math.Max(Math.Abs(l), Math.Abs(r));
                    if (a > peak) peak = a;
                }
                track.MeterLevel = any || peak > 0.0001f ? peak : 0;
            }

            if (metronome && MetronomeOn && _project != null)
                AddClick(output, frames, start, sampleRate, _project.Bpm);

            float master = (float)(_project?.MasterVolume ?? 1);
            float mpeak = 0;
            for (int i = 0; i < n; i++)
            {
                float v = output[i] * master;
                // gentle soft clip above 0.9 instead of harsh digital clipping
                if (v > 0.9f) v = 0.9f + 0.1f * (float)Math.Tanh((v - 0.9f) * 10);
                else if (v < -0.9f) v = -0.9f - 0.1f * (float)Math.Tanh((-v - 0.9f) * 10);
                output[i] = v;
                float a = Math.Abs(v);
                if (a > mpeak) mpeak = a;
            }
            MasterMeter = mpeak;
        }

        private void AddClick(float[] output, int frames, long start, int sampleRate, double bpm)
        {
            double framesPerBeat = sampleRate * 60.0 / bpm;
            int clickLen = sampleRate / 30;
            long firstBeat = (long)Math.Floor((start - clickLen) / framesPerBeat);
            if (firstBeat < 0) firstBeat = 0;
            for (long beat = firstBeat; beat * framesPerBeat < start + frames; beat++)
            {
                long bs = (long)(beat * framesPerBeat);
                double freq = beat % 4 == 0 ? 1600 : 1000;
                for (long pos = Math.Max(bs, start); pos < Math.Min(bs + clickLen, start + frames); pos++)
                {
                    double t = (double)(pos - bs) / sampleRate;
                    float v = (float)(Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t * 90) * MetronomeLevel);
                    int o = (int)(pos - start) * 2;
                    output[o] += v;
                    output[o + 1] += v;
                }
            }
        }
    }
}
