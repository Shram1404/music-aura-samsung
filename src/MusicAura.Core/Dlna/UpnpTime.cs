using System.Globalization;

namespace MusicAura.Core.Dlna;

/// <summary>Parses/formats UPnP AV time strings such as <c>0:01:23</c> or <c>00:01:23.456</c>.</summary>
public static class UpnpTime
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.Equals("NOT_IMPLEMENTED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        var parts = text.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var secondsText = parts[2];
        double fraction = 0;
        var dot = secondsText.IndexOf('.');
        if (dot >= 0)
        {
            var frac = secondsText[(dot + 1)..];
            secondsText = secondsText[..dot];
            var slash = frac.IndexOf('/');
            if (slash >= 0)
            {
                // "F0/F1" fraction form.
                if (double.TryParse(frac[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var f0) &&
                    double.TryParse(frac[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var f1) && f1 > 0)
                {
                    fraction = f0 / f1;
                }
            }
            else if (frac.Length > 0 && double.TryParse("0." + frac, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var f))
            {
                fraction = f;
            }
        }

        if (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        value = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds + fraction);
        if (negative)
        {
            value = -value;
        }

        return true;
    }

    public static string Format(TimeSpan value)
    {
        var total = Math.Max(0, (long)value.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}");
    }
}
