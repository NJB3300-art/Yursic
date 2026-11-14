using System.Collections.Generic;
using System.Linq;

namespace Yursic.Plugins
{
    /// <summary>
    /// Stock effects. They use the same JSON format as USB plugins, so they double as examples.
    /// </summary>
    public static class BuiltInEffects
    {
        private static readonly string[] Json =
        {
@"{ ""id"": ""yursic.reverb"", ""name"": ""Studio Reverb"", ""category"": ""Reverb"",
  ""params"": [
    { ""id"": ""size"", ""name"": ""Room size"", ""min"": 0, ""max"": 1, ""default"": 0.6, ""unit"": ""%"" },
    { ""id"": ""damp"", ""name"": ""Darkness"", ""min"": 0, ""max"": 1, ""default"": 0.4, ""unit"": ""%"" },
    { ""id"": ""predelay"", ""name"": ""Pre-delay"", ""min"": 0, ""max"": 120, ""default"": 15, ""unit"": ""ms"" },
    { ""id"": ""mix"", ""name"": ""Mix"", ""min"": 0, ""max"": 1, ""default"": 0.25, ""unit"": ""%"" } ],
  ""chain"": [ { ""node"": ""reverb"", ""size"": ""$size"", ""damp"": ""$damp"", ""predelay"": ""$predelay"", ""mix"": ""$mix"" } ] }",

@"{ ""id"": ""yursic.echo"", ""name"": ""Echo"", ""category"": ""Delay"",
  ""params"": [
    { ""id"": ""time"", ""name"": ""Time"", ""min"": 20, ""max"": 1500, ""default"": 375, ""unit"": ""ms"", ""curve"": ""log"" },
    { ""id"": ""feedback"", ""name"": ""Feedback"", ""min"": 0, ""max"": 0.9, ""default"": 0.35, ""unit"": ""%"" },
    { ""id"": ""tone"", ""name"": ""Tone"", ""min"": 500, ""max"": 16000, ""default"": 6000, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""pingpong"", ""name"": ""Ping-pong"", ""min"": 0, ""max"": 1, ""default"": 0 },
    { ""id"": ""mix"", ""name"": ""Mix"", ""min"": 0, ""max"": 1, ""default"": 0.3, ""unit"": ""%"" } ],
  ""chain"": [ { ""node"": ""delay"", ""time"": ""$time"", ""feedback"": ""$feedback"", ""tone"": ""$tone"", ""pingpong"": ""$pingpong"", ""mix"": ""$mix"" } ] }",

@"{ ""id"": ""yursic.overdrive"", ""name"": ""Overdrive"", ""category"": ""Distortion"",
  ""params"": [
    { ""id"": ""drive"", ""name"": ""Drive"", ""min"": 1, ""max"": 40, ""default"": 6, ""curve"": ""log"" },
    { ""id"": ""tone"", ""name"": ""Tone"", ""min"": 800, ""max"": 14000, ""default"": 5000, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""level"", ""name"": ""Level"", ""min"": -24, ""max"": 6, ""default"": -6, ""unit"": ""dB"" } ],
  ""chain"": [
    { ""node"": ""highpass"", ""freq"": 80 },
    { ""node"": ""distortion"", ""drive"": ""$drive"", ""output"": ""$level"" },
    { ""node"": ""lowpass"", ""freq"": ""$tone"", ""q"": 0.6 } ] }",

@"{ ""id"": ""yursic.compressor"", ""name"": ""Compressor"", ""category"": ""Dynamics"",
  ""params"": [
    { ""id"": ""threshold"", ""name"": ""Threshold"", ""min"": -48, ""max"": 0, ""default"": -18, ""unit"": ""dB"" },
    { ""id"": ""ratio"", ""name"": ""Ratio"", ""min"": 1, ""max"": 20, ""default"": 4, ""curve"": ""log"" },
    { ""id"": ""attack"", ""name"": ""Attack"", ""min"": 0.5, ""max"": 100, ""default"": 10, ""unit"": ""ms"", ""curve"": ""log"" },
    { ""id"": ""release"", ""name"": ""Release"", ""min"": 20, ""max"": 1000, ""default"": 150, ""unit"": ""ms"", ""curve"": ""log"" },
    { ""id"": ""makeup"", ""name"": ""Makeup"", ""min"": 0, ""max"": 24, ""default"": 4, ""unit"": ""dB"" } ],
  ""chain"": [ { ""node"": ""compressor"", ""threshold"": ""$threshold"", ""ratio"": ""$ratio"", ""attack"": ""$attack"", ""release"": ""$release"", ""makeup"": ""$makeup"" } ] }",

@"{ ""id"": ""yursic.eq3"", ""name"": ""3-Band EQ"", ""category"": ""EQ"",
  ""params"": [
    { ""id"": ""low"", ""name"": ""Low"", ""min"": -15, ""max"": 15, ""default"": 0, ""unit"": ""dB"" },
    { ""id"": ""mid"", ""name"": ""Mid"", ""min"": -15, ""max"": 15, ""default"": 0, ""unit"": ""dB"" },
    { ""id"": ""midfreq"", ""name"": ""Mid freq"", ""min"": 200, ""max"": 6000, ""default"": 1200, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""high"", ""name"": ""High"", ""min"": -15, ""max"": 15, ""default"": 0, ""unit"": ""dB"" } ],
  ""chain"": [
    { ""node"": ""lowshelf"", ""freq"": 150, ""gain"": ""$low"" },
    { ""node"": ""peak"", ""freq"": ""$midfreq"", ""q"": 0.9, ""gain"": ""$mid"" },
    { ""node"": ""highshelf"", ""freq"": 6000, ""gain"": ""$high"" } ] }",

@"{ ""id"": ""yursic.lofi"", ""name"": ""Lo-Fi"", ""category"": ""Creative"",
  ""params"": [
    { ""id"": ""bits"", ""name"": ""Bits"", ""min"": 4, ""max"": 16, ""default"": 10 },
    { ""id"": ""down"", ""name"": ""Downsample"", ""min"": 1, ""max"": 16, ""default"": 3 },
    { ""id"": ""cutoff"", ""name"": ""Cutoff"", ""min"": 800, ""max"": 12000, ""default"": 4000, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""mix"", ""name"": ""Mix"", ""min"": 0, ""max"": 1, ""default"": 0.8, ""unit"": ""%"" } ],
  ""chain"": [
    { ""node"": ""bitcrush"", ""bits"": ""$bits"", ""downsample"": ""$down"", ""mix"": ""$mix"" },
    { ""node"": ""lowpass"", ""freq"": ""$cutoff"" },
    { ""node"": ""width"", ""amount"": 0.6 } ] }",

@"{ ""id"": ""yursic.chorus"", ""name"": ""Chorus"", ""category"": ""Modulation"",
  ""params"": [
    { ""id"": ""rate"", ""name"": ""Rate"", ""min"": 0.05, ""max"": 5, ""default"": 0.8, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""depth"", ""name"": ""Depth"", ""min"": 0.5, ""max"": 15, ""default"": 5, ""unit"": ""ms"" },
    { ""id"": ""mix"", ""name"": ""Mix"", ""min"": 0, ""max"": 1, ""default"": 0.45, ""unit"": ""%"" } ],
  ""chain"": [ { ""node"": ""chorus"", ""rate"": ""$rate"", ""depth"": ""$depth"", ""mix"": ""$mix"" } ] }",

@"{ ""id"": ""yursic.tremolo"", ""name"": ""Tremolo / Auto-pan"", ""category"": ""Modulation"",
  ""params"": [
    { ""id"": ""rate"", ""name"": ""Rate"", ""min"": 0.1, ""max"": 15, ""default"": 4, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""depth"", ""name"": ""Depth"", ""min"": 0, ""max"": 1, ""default"": 0.6, ""unit"": ""%"" },
    { ""id"": ""stereo"", ""name"": ""Auto-pan"", ""min"": 0, ""max"": 1, ""default"": 0 } ],
  ""chain"": [ { ""node"": ""tremolo"", ""rate"": ""$rate"", ""depth"": ""$depth"", ""stereo"": ""$stereo"" } ] }",

@"{ ""id"": ""yursic.filter"", ""name"": ""Filter"", ""category"": ""Filter"",
  ""params"": [
    { ""id"": ""lowcut"", ""name"": ""Low cut"", ""min"": 20, ""max"": 2000, ""default"": 20, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""highcut"", ""name"": ""High cut"", ""min"": 500, ""max"": 20000, ""default"": 20000, ""unit"": ""Hz"", ""curve"": ""log"" },
    { ""id"": ""res"", ""name"": ""Resonance"", ""min"": 0.5, ""max"": 8, ""default"": 0.707, ""curve"": ""log"" } ],
  ""chain"": [
    { ""node"": ""highpass"", ""freq"": ""$lowcut"", ""q"": ""$res"" },
    { ""node"": ""lowpass"", ""freq"": ""$highcut"", ""q"": ""$res"" } ] }",
        };

        private static List<PluginDefinition> _all;

        public static IReadOnlyList<PluginDefinition> All
        {
            get
            {
                if (_all == null)
                    _all = Json.Select(j =>
                    {
                        var d = PluginDefinition.Parse(j);
                        d.IsBuiltIn = true;
                        d.Author = "Yursic";
                        return d;
                    }).ToList();
                return _all;
            }
        }
    }
}
