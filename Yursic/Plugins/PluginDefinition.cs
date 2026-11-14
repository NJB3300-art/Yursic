using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Data.Json;
using Windows.Storage;
using Yursic.Dsp;

namespace Yursic.Plugins
{
    public sealed class ParamDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double Min { get; set; }
        public double Max { get; set; } = 1;
        public double Default { get; set; }
        public string Unit { get; set; } = "";
        /// <summary>"linear" or "log" (log is good for frequencies and times).</summary>
        public string Curve { get; set; } = "linear";
    }

    /// <summary>A node value: either a constant, or "a * $param + b".</summary>
    public sealed class ValueBinding
    {
        private static readonly Regex Expr = new Regex(
            @"^\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*(?:\*\s*(-?[0-9]*\.?[0-9]+))?\s*(?:([+-])\s*([0-9]*\.?[0-9]+))?\s*$");

        public double Constant { get; private set; }
        public string ParamId { get; private set; }
        public double Scale { get; private set; } = 1;
        public double Offset { get; private set; }

        public double Eval(IDictionary<string, double> values)
        {
            if (ParamId == null) return Constant;
            values.TryGetValue(ParamId, out var v);
            return v * Scale + Offset;
        }

        public static ValueBinding Parse(IJsonValue v, string where)
        {
            switch (v.ValueType)
            {
                case JsonValueType.Number:
                    return new ValueBinding { Constant = v.GetNumber() };
                case JsonValueType.Boolean:
                    return new ValueBinding { Constant = v.GetBoolean() ? 1 : 0 };
                case JsonValueType.String:
                    {
                        var m = Expr.Match(v.GetString());
                        if (!m.Success) throw new FormatException($"{where}: can't read \"{v.GetString()}\". Use a number, \"$param\" or \"$param*2+100\".");
                        var b = new ValueBinding { ParamId = m.Groups[1].Value };
                        if (m.Groups[2].Success) b.Scale = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        if (m.Groups[4].Success)
                        {
                            b.Offset = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                            if (m.Groups[3].Value == "-") b.Offset = -b.Offset;
                        }
                        return b;
                    }
                case JsonValueType.Object:
                    {
                        var o = v.GetObject();
                        return new ValueBinding
                        {
                            ParamId = o.GetNamedString("param"),
                            Scale = o.GetNamedNumber("scale", 1),
                            Offset = o.GetNamedNumber("offset", 0)
                        };
                    }
                default:
                    throw new FormatException($"{where}: unsupported value.");
            }
        }
    }

    public sealed class ChainNodeDef
    {
        public string Type { get; set; }
        public Dictionary<string, ValueBinding> Values { get; } = new Dictionary<string, ValueBinding>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SoundDef
    {
        public string File { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public double Bpm { get; set; }
    }

    /// <summary>Parsed plugin.json. See docs/PLUGIN_FORMAT.md.</summary>
    public sealed class PluginDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "Effect";
        public List<ParamDef> Params { get; } = new List<ParamDef>();
        public List<ChainNodeDef> Chain { get; } = new List<ChainNodeDef>();
        public List<SoundDef> Sounds { get; } = new List<SoundDef>();

        public bool IsBuiltIn { get; set; }
        public StorageFolder Folder { get; set; }

        public bool IsEffect => Chain.Count > 0;
        public bool HasSounds => Sounds.Count > 0;

        public string Kind => IsEffect && HasSounds ? "Effect + sounds" : IsEffect ? "Effect" : "Sound pack";
        public string Byline => string.IsNullOrEmpty(Author) ? $"v{Version}" : $"v{Version} · {Author}";
        public string Summary => IsEffect
            ? $"{Category} · {Params.Count} controls"
            : $"{Sounds.Count} sounds";

        public static PluginDefinition Parse(string json)
        {
            if (!JsonObject.TryParse(json, out var o))
                throw new FormatException("plugin.json is not valid JSON.");

            var p = new PluginDefinition
            {
                Id = o.GetNamedString("id", ""),
                Name = o.GetNamedString("name", ""),
                Version = o.GetNamedString("version", "1.0.0"),
                Author = o.GetNamedString("author", ""),
                Description = o.GetNamedString("description", ""),
                Category = o.GetNamedString("category", "Effect"),
            };
            if (string.IsNullOrWhiteSpace(p.Id) || !Regex.IsMatch(p.Id, @"^[A-Za-z0-9._-]{3,80}$"))
                throw new FormatException("plugin.json needs an \"id\" made of letters, numbers, dots, dashes (3-80 chars).");
            if (string.IsNullOrWhiteSpace(p.Name))
                throw new FormatException("plugin.json needs a \"name\".");

            var paramsArr = o.GetNamedArray("params", new JsonArray());
            foreach (var pv in paramsArr)
            {
                var po = pv.GetObject();
                var def = new ParamDef
                {
                    Id = po.GetNamedString("id"),
                    Name = po.GetNamedString("name", po.GetNamedString("id")),
                    Min = po.GetNamedNumber("min", 0),
                    Max = po.GetNamedNumber("max", 1),
                    Unit = po.GetNamedString("unit", ""),
                    Curve = po.GetNamedString("curve", "linear").ToLowerInvariant(),
                };
                def.Default = Math.Min(def.Max, Math.Max(def.Min, po.GetNamedNumber("default", def.Min)));
                if (def.Max <= def.Min) throw new FormatException($"Param \"{def.Id}\": max must be above min.");
                if (def.Curve == "log" && def.Min <= 0) throw new FormatException($"Param \"{def.Id}\": log curve needs min > 0.");
                p.Params.Add(def);
            }

            var chain = o.GetNamedArray("chain", new JsonArray());
            if (chain.Count > 16) throw new FormatException("A chain can have at most 16 nodes.");
            int idx = 0;
            foreach (var nv in chain)
            {
                var no = nv.GetObject();
                var type = no.GetNamedString("node", "").ToLowerInvariant();
                if (!DspNode.Types.Contains(type))
                    throw new FormatException($"Chain node {idx}: unknown node \"{type}\". Known: {string.Join(", ", DspNode.Types)}.");
                var probe = DspNode.Create(type);
                var node = new ChainNodeDef { Type = type };
                foreach (var kv in no)
                {
                    if (kv.Key == "node") continue;
                    if (!probe.HasParam(kv.Key))
                        throw new FormatException($"Chain node {idx} ({type}): unknown setting \"{kv.Key}\".");
                    var b = ValueBinding.Parse(kv.Value, $"Chain node {idx} ({type}).{kv.Key}");
                    if (b.ParamId != null && p.Params.All(x => x.Id != b.ParamId))
                        throw new FormatException($"Chain node {idx} ({type}): \"${b.ParamId}\" isn't in params.");
                    node.Values[kv.Key] = b;
                }
                p.Chain.Add(node);
                idx++;
            }

            var sounds = o.GetNamedArray("sounds", new JsonArray());
            foreach (var sv in sounds)
            {
                var so = sv.GetObject();
                var file = so.GetNamedString("file", "");
                if (file.Contains("..") || file.StartsWith("/") || file.StartsWith("\\") || file.Contains(":"))
                    throw new FormatException($"Sound file path \"{file}\" must be relative to the plugin.");
                p.Sounds.Add(new SoundDef
                {
                    File = file,
                    Name = so.GetNamedString("name", System.IO.Path.GetFileNameWithoutExtension(file)),
                    Category = so.GetNamedString("category", "Loops"),
                    Bpm = so.GetNamedNumber("bpm", 0)
                });
            }

            if (!p.IsEffect && !p.HasSounds)
                throw new FormatException("plugin.json needs a \"chain\" (effect) or \"sounds\" (sound pack).");
            return p;
        }
    }
}
