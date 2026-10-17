namespace FileKakari;

public static class AnchorTargetResolver
{
    public static (FileEntry? Entry, string Reason, int Visited) Find(IEnumerable<FileEntry> entries, AnchorScrollState anchor)
    {
        FileEntry? previous = null;
        FileEntry? next = null;
        var visited = 0;
        foreach (var entry in entries)
        {
            visited++;
            if (string.Equals(entry.FullPath, anchor.AnchorPath, StringComparison.OrdinalIgnoreCase))
                return (entry, "exact-anchor", visited);
            if (previous is null && !string.IsNullOrEmpty(anchor.PreviousPath)
                && string.Equals(entry.FullPath, anchor.PreviousPath, StringComparison.OrdinalIgnoreCase)) previous = entry;
            if (next is null && !string.IsNullOrEmpty(anchor.NextPath)
                && string.Equals(entry.FullPath, anchor.NextPath, StringComparison.OrdinalIgnoreCase)) next = entry;
        }
        return previous is not null ? (previous, "previous-fallback", visited)
            : next is not null ? (next, "next-fallback", visited)
            : (null, "offset-fallback", visited);
    }
}
