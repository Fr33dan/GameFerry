using System.Globalization;
using System.Windows.Data;
using GameFerry.Core.Library;

namespace GameFerry.App;

/// <summary>Shows a byte count as KB/MB/GB.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? TargetLibrary.FormatSize(bytes) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
