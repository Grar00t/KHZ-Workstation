using System.Collections.Generic;

namespace KHZ.App.Trust;

internal interface IActivityReader
{
    IReadOnlyList<ActivityEvent> ReadRecent(
        int limit = 200);
}
