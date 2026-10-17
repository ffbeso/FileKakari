namespace FileKakari;

// One current snapshot and one sort configuration per list. No item event subscriptions.
public sealed class FileListGroupCache
{
    private EntrySnapshot[]? _snapshot;
    private GroupRepresentativeIndex? _representatives;
    private (GroupMode Mode, string Column, bool Ascending, bool FoldersFirst, DateTime Day) _key;
    public SimilarNameGroupIndex? SimilarNameIndex { get; private set; }
    public bool LastRepresentativeCacheHit { get; private set; }

    public void Clear()
    {
        _snapshot = null;
        SimilarNameIndex = null;
        _representatives = null;
        LastRepresentativeCacheHit = false;
    }

    private void Validate(IReadOnlyList<FileEntry> entries)
    {
        if (_snapshot is { } snapshot && snapshot.Length == entries.Count)
        {
            var matches = true;
            for (var i = 0; i < entries.Count; i++)
            {
                if (snapshot[i] != EntrySnapshot.Capture(entries[i])) { matches = false; break; }
            }
            if (matches) return;
        }

        Clear();
        _snapshot = new EntrySnapshot[entries.Count];
        for (var i = 0; i < entries.Count; i++) _snapshot[i] = EntrySnapshot.Capture(entries[i]);
    }

    public SimilarNameGroupIndex EnsureSimilarNames(IReadOnlyList<FileEntry> entries)
    {
        Validate(entries);
        return SimilarNameIndex ??= SimilarNameGroupIndex.Build(entries);
    }

    public GroupRepresentativeIndex EnsureRepresentatives(IReadOnlyList<FileEntry> entries,
        GroupMode mode, string column, bool ascending, bool foldersFirst)
    {
        Validate(entries);
        var key = (mode, ColumnLayoutService.NormalizeColumnId(column), ascending, foldersFirst,
            mode == GroupMode.Date ? DateTime.Today : default);
        LastRepresentativeCacheHit = _representatives is not null && _key == key;
        if (LastRepresentativeCacheHit) return _representatives!;

        var similar = mode == GroupMode.SimilarName
            ? SimilarNameIndex ??= SimilarNameGroupIndex.Build(entries) : null;
        _representatives = GroupRepresentativeIndex.Build(entries, mode, column, ascending, foldersFirst, similar);
        _key = key;
        return _representatives;
    }

    // Identity and order matter: representatives retain the first entry on equal keys.
    private readonly record struct EntrySnapshot(FileEntry Entry, string Name, string Path,
        bool IsDirectory, string Kind, long? Size, DateTime Modified, DateTime Created, DateTime Accessed)
    {
        public static EntrySnapshot Capture(FileEntry entry) => new(entry, entry.Name, entry.FullPath,
            entry.IsDirectory, entry.Kind, entry.Size, entry.ModifiedAt, entry.CreatedAt, entry.AccessedAt);
    }
}
