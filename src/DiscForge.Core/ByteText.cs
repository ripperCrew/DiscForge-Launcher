using System.Globalization;

namespace DiscForge.Core;

public static class ByteText
{
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
            return $"{bytes} o";

        return string.Format(CultureInfo.GetCultureInfo("fr-FR"), "{0:0.##} {1}", value, Units[unit]);
    }

    public static string FileCount(int count)
    {
        return count == 1 ? "1 fichier" : $"{count} fichiers";
    }
}
