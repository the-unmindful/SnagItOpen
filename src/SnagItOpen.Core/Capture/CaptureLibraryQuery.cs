using System.Globalization;

namespace SnagItOpen.Core.Capture;

public sealed record CaptureLibraryMetadata(Guid Id, string Name, int Width, int Height, DateTimeOffset CapturedAt, bool Pinned, long Bytes);
public enum CaptureLibraryFilter { All, Pinned, Today, ThisWeek }
public enum CaptureLibrarySort { Newest, Oldest, Name, Size }

/// <summary>In-memory library query. The caller supplies the current local offset for calendar filters.</summary>
public static class CaptureLibraryQuery
{
    public static IReadOnlyList<CaptureLibraryMetadata> Apply(IEnumerable<CaptureLibraryMetadata> entries, string? search,
        CaptureLibraryFilter filter, CaptureLibrarySort sort, DateTimeOffset now)
    {
        var today = now.Date;
        var monday = today.AddDays(-((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var tokens = Normalize(search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var query = entries.Where(e => filter switch
        {
            CaptureLibraryFilter.Pinned => e.Pinned,
            CaptureLibraryFilter.Today => e.CapturedAt.ToOffset(now.Offset).Date == today,
            CaptureLibraryFilter.ThisWeek => e.CapturedAt.ToOffset(now.Offset).Date >= monday && e.CapturedAt <= now,
            _ => true,
        }).Where(e =>
        {
            var local = e.CapturedAt.ToOffset(now.Offset);
            string haystack = Normalize($"{e.Name} {e.Width}x{e.Height} {local:yyyy-MM-dd} {local.ToString("g", CultureInfo.CurrentCulture)}");
            return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
        });
        return (sort switch
        {
            CaptureLibrarySort.Oldest => query.OrderBy(e => e.CapturedAt).ThenBy(e => e.Id),
            CaptureLibrarySort.Name => query.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Id),
            CaptureLibrarySort.Size => query.OrderByDescending(e => (long)e.Width * e.Height).ThenBy(e => e.Id),
            _ => query.OrderByDescending(e => e.CapturedAt).ThenBy(e => e.Id),
        }).ToList();
    }

    private static string Normalize(string text) => text.Replace('×', 'x').Replace(" x ", "x").Trim();
}
