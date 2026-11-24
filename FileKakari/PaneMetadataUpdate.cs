using System.IO;

namespace FileKakari;

internal static class PaneMetadataUpdate
{
    public static Dictionary<string, FileEntry>? Read(IEnumerable<string> paths, bool extraColumns,
        bool showHidden, bool showSystem)
    {
        var updates = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in paths)
            {
                var file = new FileInfo(path);
                var attributes = file.Attributes;
                if (!file.Exists || (!showHidden && attributes.HasFlag(FileAttributes.Hidden))
                    || (!showSystem && attributes.HasFlag(FileAttributes.System)))
                    return null;

                var updated = extraColumns ? FileEntry.FromFile(file) : FileEntry.FromFileBasic(file);
                updated.Kind = FileEntry.GetTypeText(updated.Name, false);
                updates.Add(path, updated);
            }
            return updates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static bool TryApply(FileListState list, GroupMode groupMode, Dictionary<string, FileEntry> updates)
    {
        // Relative date buckets can also change at midnight without a file event.
        if (groupMode == GroupMode.Date && list.LastLoadedAt?.LocalDateTime.Date != DateTime.Today)
            return false;
        var comparer = new FileEntryComparer(list.DisplaySortColumn, true, false);
        var matches = new List<(FileEntry Current, FileEntry Updated)>(updates.Count);
        foreach (var entry in list.Items)
        {
            if (!updates.TryGetValue(entry.FullPath, out var updated)) continue;
            if (entry.IsDirectory || entry.Name != updated.Name || comparer.Compare(entry, updated) != 0
                || (groupMode == GroupMode.Date
                    && FileGroupHelper.GetGroupKey(entry, groupMode) != FileGroupHelper.GetGroupKey(updated, groupMode)))
                return false;
            matches.Add((entry, updated));
            if (matches.Count == updates.Count) break;
        }

        // Validate the entire batch before notifying any bindings.
        if (matches.Count != updates.Count) return false;
        foreach (var (entry, updated) in matches)
        {
            entry.ModifiedAt = updated.ModifiedAt;
            entry.Size = updated.Size;
            entry.CreatedAt = updated.CreatedAt;
            entry.AccessedAt = updated.AccessedAt;
            entry.AttributesText = updated.AttributesText;
        }
        return true;
    }
}
