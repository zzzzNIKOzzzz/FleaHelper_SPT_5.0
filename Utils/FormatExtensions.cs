using System.Globalization;

namespace FleaHelper.Utils;

public static class FormatExtensions
{
    public static string FormatSeparate(this float value)
    {
        return ((int)value).ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string FormatSeparate(this int value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
