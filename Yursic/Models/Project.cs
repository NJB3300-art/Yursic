using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Yursic.Audio;
using Yursic.Helpers;
using Yursic.Plugins;

namespace Yursic.Models
{
    public sealed class Project : Observable
    {
        private string _name = "Untitled";
        private double _bpm = 120;
        private double _masterVolume = 0.9;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;
        public StorageFolder Folder { get; set; }
        public StorageFolder AudioFolder { get; set; }

        public string Name { get => _name; set => Set(ref _name, value); }
        public double Bpm { get => _bpm; set => Set(ref _bpm, Math.Max(40, Math.Min(240, value))); }
        public double MasterVolume { get => _masterVolume; set => Set(ref _masterVolume, value); }

        public ObservableCollection<Track> Tracks { get; } = new ObservableCollection<Track>();

        public double LengthSeconds
        {
            get
            {
                double end = 0;
                foreach (var t in Tracks)
                    foreach (var c in t.Clips)
                        end = Math.Max(end, c.EndSeconds);
                return end;
            }
        }

        public static readonly string[] TrackColors =
            { "#7C5CFF", "#00D4AA", "#FF8A3D", "#FF4F8B", "#3DA9FF", "#C6E83A", "#FFC83D", "#B07CFF" };

        public Track AddTrack(string name = null)
        {
            var t = new Track
            {
                Name = name ?? $"Track {Tracks.Count + 1}",
                ColorHex = TrackColors[Tracks.Count % TrackColors.Length]
            };
            Tracks.Add(t);
            return t;
        }
    }

    public sealed class Track : Observable
    {
        private string _name;
        private string _colorHex = "#7C5CFF";
        private double _volume = 0.8, _pan;
        private bool _mute, _solo, _armed, _selected;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get => _name; set => Set(ref _name, value); }

        public string ColorHex
        {
            get => _colorHex;
            set { if (Set(ref _colorHex, value)) { Raise(nameof(Brush)); Raise(nameof(ClipBrush)); } }
        }

        public SolidColorBrush Brush => new SolidColorBrush(ParseColor(_colorHex, 255));
        public SolidColorBrush ClipBrush => new SolidColorBrush(ParseColor(_colorHex, 90));

        public double Volume { get => _volume; set => Set(ref _volume, value); }
        public double Pan { get => _pan; set => Set(ref _pan, value); }
        public bool Mute { get => _mute; set => Set(ref _mute, value); }
        public bool Solo { get => _solo; set => Set(ref _solo, value); }
        public bool Armed { get => _armed; set => Set(ref _armed, value); }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

        public ObservableCollection<Clip> Clips { get; } = new ObservableCollection<Clip>();
        public ObservableCollection<EffectInstance> Effects { get; } = new ObservableCollection<EffectInstance>();

        /// <summary>Written by the audio thread, read by the UI meter timer.</summary>
        public volatile float MeterLevel;

        private double _meterValue;
        /// <summary>0..1 meter for binding; refreshed by <see cref="PollMeter"/> on the UI thread.</summary>
        public double MeterValue => _meterValue;

        public void PollMeter()
        {
            double v = Math.Min(1, MeterLevel);
            double next = v > _meterValue ? v : _meterValue * 0.85; // fast attack, smooth fall
            if (Math.Abs(next - _meterValue) > 0.005 || (next == 0 && _meterValue != 0))
            {
                _meterValue = next < 0.01 ? 0 : next;
                Raise(nameof(MeterValue));
            }
        }

        public static Color ParseColor(string hex, byte alpha)
        {
            try
            {
                hex = hex.TrimStart('#');
                return Color.FromArgb(alpha,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { return Color.FromArgb(alpha, 124, 92, 255); }
        }
    }

    public sealed class Clip : Observable
    {
        private double _start, _offset, _length, _gain = 1;
        private bool _selected;
        private string _name;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; }
        public SampleBuffer Buffer { get; set; }

        public string Name { get => _name; set => Set(ref _name, value); }
        public double StartSeconds { get => _start; set { if (Set(ref _start, Math.Max(0, value))) Raise(nameof(EndSeconds)); } }
        public double OffsetSeconds { get => _offset; set => Set(ref _offset, Math.Max(0, value)); }
        public double LengthSeconds { get => _length; set { if (Set(ref _length, Math.Max(0.01, value))) Raise(nameof(EndSeconds)); } }
        public double Gain { get => _gain; set => Set(ref _gain, value); }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        public double EndSeconds => _start + _length;

        public Clip CloneAt(double start) => new Clip
        {
            FileName = FileName,
            Buffer = Buffer,
            Name = Name,
            StartSeconds = start,
            OffsetSeconds = OffsetSeconds,
            LengthSeconds = LengthSeconds,
            Gain = Gain
        };
    }

    // ---- On-disk format (project.json) ----

    [DataContract]
    public sealed class ProjectFile
    {
        [DataMember] public int Format { get; set; } = 1;
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public double Bpm { get; set; }
        [DataMember] public double MasterVolume { get; set; }
        [DataMember] public string Created { get; set; }
        [DataMember] public string Modified { get; set; }
        [DataMember] public List<TrackFile> Tracks { get; set; } = new List<TrackFile>();
    }

    [DataContract]
    public sealed class TrackFile
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Color { get; set; }
        [DataMember] public double Volume { get; set; }
        [DataMember] public double Pan { get; set; }
        [DataMember] public bool Mute { get; set; }
        [DataMember] public bool Solo { get; set; }
        [DataMember] public List<ClipFile> Clips { get; set; } = new List<ClipFile>();
        [DataMember] public List<EffectFile> Effects { get; set; } = new List<EffectFile>();
    }

    [DataContract]
    public sealed class ClipFile
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string File { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public double Start { get; set; }
        [DataMember] public double Offset { get; set; }
        [DataMember] public double Length { get; set; }
        [DataMember] public double Gain { get; set; }
    }

    [DataContract]
    public sealed class EffectFile
    {
        [DataMember] public string PluginId { get; set; }
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public List<ParamValueFile> Values { get; set; } = new List<ParamValueFile>();
    }

    [DataContract]
    public sealed class ParamValueFile
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public double Value { get; set; }
    }
}
