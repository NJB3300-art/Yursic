using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Yursic.Helpers
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool b = value is bool v && v;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>Selected rows get the lighter surface, others the normal one.</summary>
    public sealed class SelectedBrushConverter : IValueConverter
    {
        public Brush Selected { get; set; }
        public Brush Normal { get; set; }
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is bool b && b ? Selected : Normal;
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>0..1 gain to a dB label.</summary>
    public sealed class GainToDbConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            double g = value is double d ? d : 0;
            if (g <= 0.0001) return "-∞ dB";
            return $"{20 * Math.Log10(g):0.0} dB";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    public sealed class PanToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            double p = value is double d ? d : 0;
            if (Math.Abs(p) < 0.02) return "C";
            return p < 0 ? $"L{-p * 100:0}" : $"R{p * 100:0}";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
