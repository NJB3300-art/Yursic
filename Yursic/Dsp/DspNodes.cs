using System;
using System.Collections.Generic;

namespace Yursic.Dsp
{
    /// <summary>
    /// Building block for effects. Plugins describe an effect as a chain of these nodes,
    /// so plugin files never contain executable code (UWP/Xbox can't load native plugin DLLs).
    /// Buffers are stereo interleaved floats.
    /// </summary>
    public abstract class DspNode
    {
        private readonly Dictionary<string, Action<double>> _setters = new Dictionary<string, Action<double>>(StringComparer.OrdinalIgnoreCase);
        protected int SampleRate { get; private set; } = 48000;
        private bool _prepared;

        protected void Param(string name, Action<double> setter) => _setters[name] = setter;

        public bool HasParam(string name) => _setters.ContainsKey(name);

        public void Set(string name, double value)
        {
            if (_setters.TryGetValue(name, out var s))
            {
                s(value);
                if (_prepared) Update();
            }
        }

        public void Prepare(int sampleRate)
        {
            if (_prepared && sampleRate == SampleRate) return;
            SampleRate = sampleRate;
            _prepared = true;
            Allocate();
            Update();
        }

        /// <summary>(Re)allocate buffers for the current sample rate.</summary>
        protected virtual void Allocate() { }

        /// <summary>Recompute coefficients after a parameter or rate change.</summary>
        protected virtual void Update() { }

        public virtual void Reset() { Allocate(); Update(); }

        public abstract void Process(float[] buf, int frames);

        protected static float DbToGain(double db) => (float)Math.Pow(10, db / 20.0);

        public static readonly string[] Types =
        {
            "gain", "lowpass", "highpass", "bandpass", "peak", "lowshelf", "highshelf",
            "delay", "distortion", "bitcrush", "compressor", "reverb", "chorus", "tremolo", "width"
        };

        public static DspNode Create(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "gain": return new GainNode();
                case "lowpass":
                case "highpass":
                case "bandpass":
                case "peak":
                case "lowshelf":
                case "highshelf": return new BiquadNode(type.ToLowerInvariant());
                case "delay": return new DelayNode();
                case "distortion": return new DistortionNode();
                case "bitcrush": return new BitcrushNode();
                case "compressor": return new CompressorNode();
                case "reverb": return new ReverbNode();
                case "chorus": return new ChorusNode();
                case "tremolo": return new TremoloNode();
                case "width": return new WidthNode();
                default: return null;
            }
        }
    }

    /// <summary>gain: db</summary>
    public sealed class GainNode : DspNode
    {
        private double _db;
        private float _g = 1;
        public GainNode() { Param("db", v => _db = v); }
        protected override void Update() => _g = DbToGain(_db);
        public override void Process(float[] b, int frames)
        {
            for (int i = 0; i < frames * 2; i++) b[i] *= _g;
        }
    }

    /// <summary>RBJ biquad. freq (Hz), q, gain (dB, for peak/shelf), mix.</summary>
    public sealed class BiquadNode : DspNode
    {
        private readonly string _kind;
        private double _freq = 1000, _q = 0.707, _gainDb, _mix = 1;
        private double b0, b1, b2, a1, a2;
        private double x1L, x2L, y1L, y2L, x1R, x2R, y1R, y2R;

        public BiquadNode(string kind)
        {
            _kind = kind;
            Param("freq", v => _freq = v);
            Param("q", v => _q = Math.Max(0.05, v));
            Param("gain", v => _gainDb = v);
            Param("mix", v => _mix = v);
        }

        protected override void Allocate() { x1L = x2L = y1L = y2L = x1R = x2R = y1R = y2R = 0; }

        protected override void Update()
        {
            double f = Math.Min(Math.Max(_freq, 10), SampleRate * 0.45);
            double w0 = 2 * Math.PI * f / SampleRate;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2 * _q);
            double A = Math.Pow(10, _gainDb / 40);
            double a0;
            switch (_kind)
            {
                case "lowpass":
                    b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                case "highpass":
                    b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                case "bandpass":
                    b0 = alpha; b1 = 0; b2 = -alpha;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
                case "peak":
                    b0 = 1 + alpha * A; b1 = -2 * cos; b2 = 1 - alpha * A;
                    a0 = 1 + alpha / A; a1 = -2 * cos; a2 = 1 - alpha / A; break;
                case "lowshelf":
                    {
                        double s = 2 * Math.Sqrt(A) * alpha;
                        b0 = A * ((A + 1) - (A - 1) * cos + s);
                        b1 = 2 * A * ((A - 1) - (A + 1) * cos);
                        b2 = A * ((A + 1) - (A - 1) * cos - s);
                        a0 = (A + 1) + (A - 1) * cos + s;
                        a1 = -2 * ((A - 1) + (A + 1) * cos);
                        a2 = (A + 1) + (A - 1) * cos - s;
                        break;
                    }
                default: // highshelf
                    {
                        double s = 2 * Math.Sqrt(A) * alpha;
                        b0 = A * ((A + 1) + (A - 1) * cos + s);
                        b1 = -2 * A * ((A - 1) + (A + 1) * cos);
                        b2 = A * ((A + 1) + (A - 1) * cos - s);
                        a0 = (A + 1) - (A - 1) * cos + s;
                        a1 = 2 * ((A - 1) - (A + 1) * cos);
                        a2 = (A + 1) - (A - 1) * cos - s;
                        break;
                    }
            }
            b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        }

        public override void Process(float[] b, int frames)
        {
            float wet = (float)_mix, dry = 1 - wet;
            for (int i = 0; i < frames; i++)
            {
                double xl = b[2 * i], xr = b[2 * i + 1];
                double yl = b0 * xl + b1 * x1L + b2 * x2L - a1 * y1L - a2 * y2L;
                x2L = x1L; x1L = xl; y2L = y1L; y1L = yl;
                double yr = b0 * xr + b1 * x1R + b2 * x2R - a1 * y1R - a2 * y2R;
                x2R = x1R; x1R = xr; y2R = y1R; y1R = yr;
                b[2 * i] = (float)(xl * dry + yl * wet);
                b[2 * i + 1] = (float)(xr * dry + yr * wet);
            }
            // flush denormals
            if (Math.Abs(y1L) < 1e-20) y1L = 0;
            if (Math.Abs(y1R) < 1e-20) y1R = 0;
        }
    }

    /// <summary>delay: time (ms), feedback (0-0.95), mix (0-1), pingpong (0/1), tone (Hz lowpass on repeats)</summary>
    public sealed class DelayNode : DspNode
    {
        private double _timeMs = 350, _feedback = 0.35, _mix = 0.3, _tone = 8000;
        private bool _pingPong;
        private float[] _l, _r;
        private int _pos, _delay;
        private float _lpL, _lpR, _lpCoef;

        public DelayNode()
        {
            Param("time", v => _timeMs = v);
            Param("feedback", v => _feedback = Math.Min(0.95, Math.Max(0, v)));
            Param("mix", v => _mix = v);
            Param("pingpong", v => _pingPong = v >= 0.5);
            Param("tone", v => _tone = v);
        }

        protected override void Allocate()
        {
            int max = SampleRate * 2 + 1;
            _l = new float[max];
            _r = new float[max];
            _pos = 0;
            _lpL = _lpR = 0;
        }

        protected override void Update()
        {
            _delay = (int)Math.Min(_l.Length - 1, Math.Max(1, _timeMs * SampleRate / 1000.0));
            _lpCoef = (float)Math.Exp(-2 * Math.PI * Math.Min(_tone, SampleRate * 0.45) / SampleRate);
        }

        public override void Process(float[] b, int frames)
        {
            int n = _l.Length;
            float fb = (float)_feedback, wet = (float)_mix, dry = 1 - wet * 0.5f;
            for (int i = 0; i < frames; i++)
            {
                int read = _pos - _delay;
                if (read < 0) read += n;
                float dl = _l[read], dr = _r[read];
                _lpL = dl + (_lpL - dl) * _lpCoef;
                _lpR = dr + (_lpR - dr) * _lpCoef;
                float inL = b[2 * i], inR = b[2 * i + 1];
                if (_pingPong)
                {
                    _l[_pos] = (inL + inR) * 0.5f + _lpR * fb;
                    _r[_pos] = _lpL * fb;
                }
                else
                {
                    _l[_pos] = inL + _lpL * fb;
                    _r[_pos] = inR + _lpR * fb;
                }
                b[2 * i] = inL * dry + dl * wet;
                b[2 * i + 1] = inR * dry + dr * wet;
                if (++_pos >= n) _pos = 0;
            }
        }
    }

    /// <summary>distortion: drive (1-50), mix, output (dB)</summary>
    public sealed class DistortionNode : DspNode
    {
        private double _drive = 4, _mix = 1, _outDb;
        private float _out = 1;
        public DistortionNode()
        {
            Param("drive", v => _drive = Math.Max(1, v));
            Param("mix", v => _mix = v);
            Param("output", v => _outDb = v);
        }
        protected override void Update() => _out = DbToGain(_outDb);
        public override void Process(float[] b, int frames)
        {
            float d = (float)_drive, wet = (float)_mix, dry = 1 - wet;
            float norm = 1f / (float)Math.Tanh(d);
            for (int i = 0; i < frames * 2; i++)
            {
                float x = b[i];
                float y = (float)Math.Tanh(x * d) * norm;
                b[i] = (x * dry + y * wet) * _out;
            }
        }
    }

    /// <summary>bitcrush: bits (2-16), downsample (1-32), mix</summary>
    public sealed class BitcrushNode : DspNode
    {
        private double _bits = 8, _down = 4, _mix = 1;
        private int _count;
        private float _holdL, _holdR;
        public BitcrushNode()
        {
            Param("bits", v => _bits = Math.Min(16, Math.Max(2, v)));
            Param("downsample", v => _down = Math.Min(32, Math.Max(1, v)));
            Param("mix", v => _mix = v);
        }
        public override void Process(float[] b, int frames)
        {
            float steps = (float)Math.Pow(2, _bits - 1);
            int down = (int)_down;
            float wet = (float)_mix, dry = 1 - wet;
            for (int i = 0; i < frames; i++)
            {
                if (_count++ % down == 0)
                {
                    _holdL = (float)Math.Round(b[2 * i] * steps) / steps;
                    _holdR = (float)Math.Round(b[2 * i + 1] * steps) / steps;
                }
                b[2 * i] = b[2 * i] * dry + _holdL * wet;
                b[2 * i + 1] = b[2 * i + 1] * dry + _holdR * wet;
            }
        }
    }

    /// <summary>compressor: threshold (dB), ratio, attack (ms), release (ms), makeup (dB)</summary>
    public sealed class CompressorNode : DspNode
    {
        private double _thresh = -18, _ratio = 4, _attack = 10, _release = 120, _makeupDb;
        private double _env, _att, _rel;
        private float _makeup = 1;
        public CompressorNode()
        {
            Param("threshold", v => _thresh = v);
            Param("ratio", v => _ratio = Math.Max(1, v));
            Param("attack", v => _attack = Math.Max(0.1, v));
            Param("release", v => _release = Math.Max(1, v));
            Param("makeup", v => _makeupDb = v);
        }
        protected override void Allocate() { _env = 0; }
        protected override void Update()
        {
            _att = Math.Exp(-1.0 / (_attack * 0.001 * SampleRate));
            _rel = Math.Exp(-1.0 / (_release * 0.001 * SampleRate));
            _makeup = DbToGain(_makeupDb);
        }
        public override void Process(float[] b, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                double x = Math.Max(Math.Abs(b[2 * i]), Math.Abs(b[2 * i + 1]));
                double c = x > _env ? _att : _rel;
                _env = x + (_env - x) * c;
                double db = 20 * Math.Log10(_env + 1e-9);
                double over = db - _thresh;
                double gainDb = over > 0 ? -over * (1 - 1 / _ratio) : 0;
                float g = (float)Math.Pow(10, gainDb / 20) * _makeup;
                b[2 * i] *= g;
                b[2 * i + 1] *= g;
            }
        }
    }

    /// <summary>Freeverb-style reverb: size (0-1), damp (0-1), mix (0-1), width (0-1), predelay (ms)</summary>
    public sealed class ReverbNode : DspNode
    {
        private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
        private const int Spread = 23;

        private double _size = 0.6, _damp = 0.4, _mix = 0.3, _width = 1, _predelayMs;
        private Comb[] _combL, _combR;
        private Allpass[] _apL, _apR;
        private float[] _pre;
        private int _prePos, _preLen;

        public ReverbNode()
        {
            Param("size", v => _size = Math.Min(1, Math.Max(0, v)));
            Param("damp", v => _damp = Math.Min(1, Math.Max(0, v)));
            Param("mix", v => _mix = Math.Min(1, Math.Max(0, v)));
            Param("width", v => _width = Math.Min(1, Math.Max(0, v)));
            Param("predelay", v => _predelayMs = Math.Min(200, Math.Max(0, v)));
        }

        protected override void Allocate()
        {
            double scale = SampleRate / 44100.0;
            _combL = new Comb[CombTuning.Length];
            _combR = new Comb[CombTuning.Length];
            for (int i = 0; i < CombTuning.Length; i++)
            {
                _combL[i] = new Comb((int)(CombTuning[i] * scale));
                _combR[i] = new Comb((int)((CombTuning[i] + Spread) * scale));
            }
            _apL = new Allpass[AllpassTuning.Length];
            _apR = new Allpass[AllpassTuning.Length];
            for (int i = 0; i < AllpassTuning.Length; i++)
            {
                _apL[i] = new Allpass((int)(AllpassTuning[i] * scale));
                _apR[i] = new Allpass((int)((AllpassTuning[i] + Spread) * scale));
            }
            _pre = new float[(int)(0.2 * SampleRate) * 2 + 2];
            _prePos = 0;
        }

        protected override void Update()
        {
            float fb = (float)(0.7 + 0.28 * _size);
            float damp = (float)(_damp * 0.4);
            foreach (var c in _combL) { c.Feedback = fb; c.Damp = damp; }
            foreach (var c in _combR) { c.Feedback = fb; c.Damp = damp; }
            _preLen = Math.Max(1, (int)(_predelayMs * SampleRate / 1000.0));
        }

        public override void Process(float[] b, int frames)
        {
            float wet = (float)_mix, dry = 1 - wet;
            float w1 = 3 * wet * (float)(_width / 2 + 0.5), w2 = 3 * wet * (float)((1 - _width) / 2); // Freeverb wet scale
            int preFrames = _pre.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                float inL = b[2 * i], inR = b[2 * i + 1];

                // predelay line (stereo interleaved ring buffer)
                int read = _prePos - _preLen;
                if (read < 0) read += preFrames;
                float pl = _predelayMs > 0 ? _pre[2 * read] : inL;
                float pr = _predelayMs > 0 ? _pre[2 * read + 1] : inR;
                _pre[2 * _prePos] = inL;
                _pre[2 * _prePos + 1] = inR;
                if (++_prePos >= preFrames) _prePos = 0;

                float input = (pl + pr) * 0.015f;
                float outL = 0, outR = 0;
                for (int c = 0; c < _combL.Length; c++)
                {
                    outL += _combL[c].Process(input);
                    outR += _combR[c].Process(input);
                }
                for (int a = 0; a < _apL.Length; a++)
                {
                    outL = _apL[a].Process(outL);
                    outR = _apR[a].Process(outR);
                }
                b[2 * i] = inL * dry + outL * w1 + outR * w2;
                b[2 * i + 1] = inR * dry + outR * w1 + outL * w2;
            }
        }

        private sealed class Comb
        {
            private readonly float[] _buf;
            private int _idx;
            private float _store;
            public float Feedback, Damp;
            public Comb(int size) { _buf = new float[Math.Max(1, size)]; }
            public float Process(float input)
            {
                float output = _buf[_idx];
                _store = output * (1 - Damp) + _store * Damp;
                _buf[_idx] = input + _store * Feedback;
                if (++_idx >= _buf.Length) _idx = 0;
                return output;
            }
        }

        private sealed class Allpass
        {
            private readonly float[] _buf;
            private int _idx;
            public Allpass(int size) { _buf = new float[Math.Max(1, size)]; }
            public float Process(float input)
            {
                float bufout = _buf[_idx];
                float output = -input + bufout;
                _buf[_idx] = input + bufout * 0.5f;
                if (++_idx >= _buf.Length) _idx = 0;
                return output;
            }
        }
    }

    /// <summary>chorus: rate (Hz), depth (ms), mix</summary>
    public sealed class ChorusNode : DspNode
    {
        private double _rate = 0.8, _depthMs = 6, _mix = 0.4, _phase;
        private float[] _l, _r;
        private int _pos;
        public ChorusNode()
        {
            Param("rate", v => _rate = Math.Max(0.01, v));
            Param("depth", v => _depthMs = Math.Min(20, Math.Max(0.1, v)));
            Param("mix", v => _mix = v);
        }
        protected override void Allocate()
        {
            int n = (int)(0.05 * SampleRate);
            _l = new float[n];
            _r = new float[n];
            _pos = 0;
        }
        private static float Read(float[] buf, int pos, double delay)
        {
            double rp = pos - delay;
            while (rp < 0) rp += buf.Length;
            int i0 = (int)rp, i1 = (i0 + 1) % buf.Length;
            float t = (float)(rp - i0);
            return buf[i0 % buf.Length] * (1 - t) + buf[i1] * t;
        }
        public override void Process(float[] b, int frames)
        {
            double baseDelay = 0.012 * SampleRate, depth = _depthMs * 0.001 * SampleRate;
            double inc = 2 * Math.PI * _rate / SampleRate;
            float wet = (float)_mix, dry = 1 - wet * 0.5f;
            for (int i = 0; i < frames; i++)
            {
                _l[_pos] = b[2 * i];
                _r[_pos] = b[2 * i + 1];
                double dl = baseDelay + depth * (0.5 + 0.5 * Math.Sin(_phase));
                double dr = baseDelay + depth * (0.5 + 0.5 * Math.Cos(_phase));
                b[2 * i] = b[2 * i] * dry + Read(_l, _pos, dl) * wet;
                b[2 * i + 1] = b[2 * i + 1] * dry + Read(_r, _pos, dr) * wet;
                _phase += inc;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                if (++_pos >= _l.Length) _pos = 0;
            }
        }
    }

    /// <summary>tremolo: rate (Hz), depth (0-1), stereo (0/1 auto-pan)</summary>
    public sealed class TremoloNode : DspNode
    {
        private double _rate = 4, _depth = 0.5, _phase;
        private bool _stereo;
        public TremoloNode()
        {
            Param("rate", v => _rate = Math.Max(0.01, v));
            Param("depth", v => _depth = Math.Min(1, Math.Max(0, v)));
            Param("stereo", v => _stereo = v >= 0.5);
        }
        public override void Process(float[] b, int frames)
        {
            double inc = 2 * Math.PI * _rate / SampleRate;
            for (int i = 0; i < frames; i++)
            {
                double s = 0.5 + 0.5 * Math.Sin(_phase);
                float gl = (float)(1 - _depth * s);
                float gr = _stereo ? (float)(1 - _depth * (1 - s)) : gl;
                b[2 * i] *= gl;
                b[2 * i + 1] *= gr;
                _phase += inc;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            }
        }
    }

    /// <summary>width: amount (0 = mono, 1 = unchanged, 2 = extra wide)</summary>
    public sealed class WidthNode : DspNode
    {
        private double _amount = 1;
        public WidthNode() { Param("amount", v => _amount = Math.Min(2, Math.Max(0, v))); }
        public override void Process(float[] b, int frames)
        {
            float w = (float)_amount;
            for (int i = 0; i < frames; i++)
            {
                float m = (b[2 * i] + b[2 * i + 1]) * 0.5f;
                float s = (b[2 * i] - b[2 * i + 1]) * 0.5f * w;
                b[2 * i] = m + s;
                b[2 * i + 1] = m - s;
            }
        }
    }
}
