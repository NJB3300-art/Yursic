using System;

namespace Yursic.Helpers
{
    public static class Format
    {
        public static string Bytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.0} {units[u]}";
        }

        public static string Time(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }

        /// <summary>Bar.Beat display (1-based), e.g. "3.2".</summary>
        public static string BarBeat(double seconds, double bpm, int beatsPerBar = 4)
        {
            if (seconds < 0) seconds = 0;
            double beats = seconds * bpm / 60.0;
            int bar = (int)(beats / beatsPerBar) + 1;
            int beat = (int)(beats % beatsPerBar) + 1;
            return $"{bar}.{beat}";
        }

        public static string Relative(DateTimeOffset when)
        {
            var d = DateTimeOffset.Now - when;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
            if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
            return when.ToString("MMM d, yyyy");
        }
    }
}
