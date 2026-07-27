using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace IconSetter.Converters
{
    /// <summary>
    /// The original app tried to color status text with WPF DataTriggers that did an exact
    /// string match (Value="Error"). Since real status strings look like "Error: message",
    /// those triggers never fired. This converter matches on prefix instead.
    /// </summary>
    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string status = value as string ?? string.Empty;

            if (status.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                return new SolidColorBrush(Color.FromRgb(0xD9, 0x43, 0x5A));

            if (status.StartsWith("Applied", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Removed", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Reverted", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Converted", StringComparison.OrdinalIgnoreCase))
                return new SolidColorBrush(Color.FromRgb(0x1E, 0x9E, 0x5A));

            if (status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Pending", StringComparison.OrdinalIgnoreCase))
                return new SolidColorBrush(Color.FromRgb(0x6B, 0x6E, 0x7E));

            if (status.StartsWith("Processing", StringComparison.OrdinalIgnoreCase))
                return new SolidColorBrush(Color.FromRgb(0xDB, 0x9A, 0x2C));

            return new SolidColorBrush(Color.FromRgb(0x6B, 0x6E, 0x7E));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
