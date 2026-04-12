using System;
using Microsoft.UI.Xaml.Data;

namespace Zink.Converters
{
    public sealed class TimeSpanToClockTextConverter : IValueConverter
    {
        // ConverterParameter "Live" => show "Live" for null.
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is TimeSpan ts) return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
            if (parameter is string p && p.Equals("Live", StringComparison.OrdinalIgnoreCase)) return "Live";
            return "00:00";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
}
