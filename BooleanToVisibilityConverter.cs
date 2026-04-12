using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Zink
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                bool invert = parameter as string == "Invert";
                return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility vis)
            {
                bool invert = parameter as string == "Invert";
                return invert ? vis != Visibility.Visible : vis == Visibility.Visible;
            }
            return false;
        }
    }
}
