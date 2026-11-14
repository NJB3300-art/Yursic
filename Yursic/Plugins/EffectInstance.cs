using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Yursic.Dsp;
using Yursic.Helpers;

namespace Yursic.Plugins
{
    /// <summary>One effect in a track's chain: a plugin definition plus live DSP state.</summary>
    public sealed class EffectInstance : Observable
    {
        private readonly List<KeyValuePair<DspNode, ChainNodeDef>> _nodes = new List<KeyValuePair<DspNode, ChainNodeDef>>();
        private readonly Dictionary<string, double> _values = new Dictionary<string, double>();
        private bool _enabled = true;

        public EffectInstance(PluginDefinition def)
        {
            Definition = def;
            foreach (var p in def.Params) _values[p.Id] = p.Default;
            foreach (var n in def.Chain)
                _nodes.Add(new KeyValuePair<DspNode, ChainNodeDef>(DspNode.Create(n.Type), n));
            foreach (var kv in _nodes)
                foreach (var v in kv.Value.Values)
                    kv.Key.Set(v.Key, v.Value.Eval(_values));
            Controls = new ObservableCollection<EffectControl>(def.Params.Select(p => new EffectControl(this, p)));
        }

        public PluginDefinition Definition { get; }
        public string Name => Definition.Name;
        public ObservableCollection<EffectControl> Controls { get; }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public IReadOnlyDictionary<string, double> Values => _values;

        public double GetValue(string id) => _values.TryGetValue(id, out var v) ? v : 0;

        public void SetValue(string id, double value)
        {
            _values[id] = value;
            foreach (var kv in _nodes)
                foreach (var v in kv.Value.Values)
                    if (v.Value.ParamId == id)
                        kv.Key.Set(v.Key, v.Value.Eval(_values));
        }

        /// <summary>Called on the audio thread.</summary>
        public void Process(float[] buf, int frames, int sampleRate)
        {
            if (!_enabled) return;
            for (int i = 0; i < _nodes.Count; i++)
            {
                var n = _nodes[i].Key;
                n.Prepare(sampleRate);
                n.Process(buf, frames);
            }
        }

        public void Reset()
        {
            foreach (var kv in _nodes) kv.Key.Reset();
        }
    }

    /// <summary>A slider bound to one plugin parameter. Slider runs 0..1000; mapping handles log curves.</summary>
    public sealed class EffectControl : Observable
    {
        private readonly EffectInstance _owner;

        public EffectControl(EffectInstance owner, ParamDef def)
        {
            _owner = owner;
            Def = def;
        }

        public ParamDef Def { get; }
        public string Name => Def.Name;

        public double Value
        {
            get => _owner.GetValue(Def.Id);
            set
            {
                double v = Math.Min(Def.Max, Math.Max(Def.Min, value));
                _owner.SetValue(Def.Id, v);
                Raise();
                Raise(nameof(SliderValue));
                Raise(nameof(Display));
            }
        }

        public double SliderValue
        {
            get
            {
                double v = Value;
                double t = Def.Curve == "log"
                    ? Math.Log(v / Def.Min) / Math.Log(Def.Max / Def.Min)
                    : (v - Def.Min) / (Def.Max - Def.Min);
                return Math.Round(t * 1000);
            }
            set
            {
                double t = Math.Min(1, Math.Max(0, value / 1000.0));
                Value = Def.Curve == "log"
                    ? Def.Min * Math.Pow(Def.Max / Def.Min, t)
                    : Def.Min + (Def.Max - Def.Min) * t;
            }
        }

        public string Display
        {
            get
            {
                double v = Value;
                string unit = Def.Unit ?? "";
                if (unit == "%") return $"{v * 100:0}%";
                if (unit == "Hz" && v >= 1000) return $"{v / 1000:0.0} kHz";
                if (Math.Abs(v) >= 100) return $"{v:0} {unit}".Trim();
                if (Math.Abs(v) >= 10) return $"{v:0.0} {unit}".Trim();
                return $"{v:0.00} {unit}".Trim();
            }
        }
    }
}
