using System.ComponentModel;

namespace FileKakari;

internal sealed class StatusSummaryService
{
    private readonly LocalizationService _text;

    public StatusSummaryService(LocalizationService text)
    {
        _text = text;
    }

    public string BuildStatusSummary(
        string currentFolderSummary,
        string? statusMessagePrefix,
        IReadOnlyList<FileEntry> selectedEntries,
        string? selectedSizeText,
        long? elapsedMs = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(statusMessagePrefix))
        {
            parts.Add(statusMessagePrefix);
        }

        parts.Add(currentFolderSummary);
        if (selectedEntries.Count > 0)
        {
            parts.Add(_text.Format("StatusSelectionSummary", selectedEntries.Count, selectedSizeText ?? "..."));
        }

        if (elapsedMs is { } ms)
        {
            parts.Add($"{ms:N0} ms");
        }

        return string.Join(" | ", parts);
    }

    public string BuildCurrentViewSummary(
        IReadOnlyCollection<FileEntry> items,
        ICollectionView itemsView,
        string? filter,
        bool statusAggregationEnabled,
        bool isSpecialLocation)
    {
        var totalCount = items.Count;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var visibleCount = statusAggregationEnabled
                ? GetVisibleItemCount(itemsView)
                : totalCount;
            var filterSummary = _text.Format("StatusFilterSummary", visibleCount, totalCount);
            return isSpecialLocation
                ? $"{filterSummary}, {BuildThisPcSummary(items)}"
                : filterSummary;
        }

        if (isSpecialLocation)
        {
            return BuildThisPcSummary(items);
        }

        return _text.Format("StatusFolderSummary", totalCount);
    }

    public static long SumKnownFileSizes(IReadOnlyList<FileEntry> entries, CancellationToken token)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.IsDirectory && entry.Size is { } size)
            {
                total += size;
            }
        }

        return total;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }

    private string BuildThisPcSummary(IReadOnlyCollection<FileEntry> items)
    {
        var readyDrives = items.Where(entry => entry.Size is not null && entry.FreeSpace is not null).ToList();
        var totalSize = readyDrives.Sum(entry => entry.Size!.Value);
        var freeSpace = readyDrives.Sum(entry => entry.FreeSpace!.Value);
        return _text.Format("StatusPcSummary", items.Count, FormatSize(freeSpace), FormatSize(totalSize));
    }

    private static int GetVisibleItemCount(ICollectionView itemsView)
    {
        var count = 0;
        foreach (var _ in itemsView)
        {
            count++;
        }

        return count;
    }
}
