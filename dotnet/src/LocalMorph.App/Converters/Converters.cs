using System.Globalization;
using LocalMorph.Core.Jobs;

namespace LocalMorph.App.Converters;

public sealed class SecondsToClockConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double seconds when double.IsFinite(seconds) && seconds >= 0 => FormatWithTenths(seconds),
        _ => "0:00.0"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return 0d;
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)) return plain;
        var parts = text.Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var component)) return 0d;
            total = total * 60 + component;
        }
        return total;
    }

    private static string FormatWithTenths(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}"
            : $"{time.Minutes}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }
}

public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? SourceFile.FormatBytes(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset when) return string.Empty;
        var delta = DateTimeOffset.Now - when;
        return delta.TotalSeconds switch
        {
            < 60 => "Just now",
            < 3600 => $"{(int)delta.TotalMinutes} min ago",
            < 86400 => $"{(int)delta.TotalHours} h ago",
            < 172800 => "Yesterday",
            _ => when.LocalDateTime.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DoubleToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number ? number.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0d;
}
