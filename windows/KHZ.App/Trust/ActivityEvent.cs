using System;
using System.Globalization;

namespace KHZ.App.Trust;

internal sealed record ActivityEvent(
    long Id,
    string OccurredLocal,
    string TimeZoneId,
    string Category,
    string Action,
    string Result,
    string? Target)
{
    public string DisplayTime
    {
        get
        {
            if (DateTimeOffset.TryParse(
                    OccurredLocal,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var value))
            {
                return value.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
            }

            return OccurredLocal;
        }
    }
}
