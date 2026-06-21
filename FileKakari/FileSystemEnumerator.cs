using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace FileKakari;

public sealed class FileSystemEnumerator : IFileEnumerator
{
    private readonly Func<AppSettings> _getSettings;

    public FileSystemEnumerator()
        : this(() => new AppSettings())
    {
    }

    public FileSystemEnumerator(Func<AppSettings> getSettings)
    {
        _getSettings = getSettings;
    }

    public IEnumerable<FileEntry> Enumerate(string path, CancellationToken cancellationToken)
    {
        return Enumerate(path, "Name", true, true, true, cancellationToken);
    }

    public IEnumerable<FileEntry> Enumerate(
        string path,
        string sortColumn,
        bool sortAscending,
        bool sortFoldersFirst,
        bool extraColumnsEnabled,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var metrics = new EnumerationMetrics();
        var createCount = 0;
        var settings = _getSettings();
        var attributesToSkip = FileAttributes.None;
        if (!settings.ShowHiddenFiles)
        {
            attributesToSkip |= FileAttributes.Hidden;
        }

        if (!settings.ShowSystemFiles)
        {
            attributesToSkip |= FileAttributes.System;
        }

        var options = new EnumerationOptions
        {
            AttributesToSkip = attributesToSkip,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        try
        {
            if (string.Equals(sortColumn, "__none", StringComparison.Ordinal))
            {
                foreach (var entry in EnumerateEntries(path, options, extraColumnsEnabled, metrics))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    createCount++;
                    yield return entry;
                }

                yield break;
            }

            if (!CanSortByPath(sortColumn))
            {
                foreach (var entry in EnumerateSortedEntries(path, options, sortColumn, sortAscending, sortFoldersFirst, extraColumnsEnabled, metrics, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    createCount++;
                    yield return entry;
                }

                yield break;
            }

            var paths = EnumerateSortedPaths(path, options, sortColumn, sortAscending, sortFoldersFirst, extraColumnsEnabled, metrics);

            foreach (var item in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = CreateEntry(item, extraColumnsEnabled, metrics);
                createCount++;
                yield return entry;
            }
        }
        finally
        {
            totalStopwatch.Stop();
            var sortKeyMs = metrics.PathSortKeyStopwatch.ElapsedMilliseconds + metrics.EntrySortKeyStopwatch.ElapsedMilliseconds;
            var rawEnumerateMs = Math.Max(0, totalStopwatch.ElapsedMilliseconds - metrics.CreateStopwatch.ElapsedMilliseconds - metrics.SortStopwatch.ElapsedMilliseconds);
            PerfLog.Write($"file-enumerate-complete path=\"{path}\" count={createCount} totalMs={totalStopwatch.ElapsedMilliseconds} rawEnumerateMs={rawEnumerateMs} fileEntryCreateMs={metrics.CreateStopwatch.ElapsedMilliseconds} metadataReadMs={metrics.MetadataStopwatch.ElapsedMilliseconds} displayStringMs={metrics.DisplayStringStopwatch.ElapsedMilliseconds} sortMs={metrics.SortStopwatch.ElapsedMilliseconds} sortKeyPrepareMs={sortKeyMs} sortColumn={sortColumn} extraColumns={extraColumnsEnabled}");
        }
    }

    private static IEnumerable<FileEntry> EnumerateEntries(
        string path,
        EnumerationOptions options,
        bool extraColumnsEnabled,
        EnumerationMetrics metrics)
    {
        var enumerable = new FileSystemEnumerable<FileEntry>(
            path,
            (ref FileSystemEntry entry) => CreateEntry(ref entry, extraColumnsEnabled, metrics),
            options);

        return enumerable;
    }

    private static FileEntry CreateEntry(FileSystemPath item, bool extraColumnsEnabled, EnumerationMetrics metrics)
    {
        metrics.CreateStopwatch.Start();
        try
        {
            metrics.DisplayStringStopwatch.Start();
            var kind = FileEntry.GetTypeText(item.Name, item.IsDirectory);
            var attributesText = extraColumnsEnabled ? FileEntry.GetAttributesText(item.Attributes) : "";
            metrics.DisplayStringStopwatch.Stop();

            return new FileEntry
            {
                Name = item.Name,
                FullPath = item.Path,
                Kind = kind,
                ModifiedAt = item.Modified,
                CreatedAt = extraColumnsEnabled ? item.Created : DateTime.MinValue,
                AccessedAt = extraColumnsEnabled ? item.Accessed : DateTime.MinValue,
                Size = item.Size,
                IsDirectory = item.IsDirectory,
                AttributesText = attributesText
            };
        }
        finally
        {
            metrics.StopNested();
            metrics.CreateStopwatch.Stop();
        }
    }

    private static FileEntry CreateEntry(ref FileSystemEntry entry, bool extraColumnsEnabled, EnumerationMetrics metrics)
    {
        metrics.CreateStopwatch.Start();
        try
        {
            metrics.MetadataStopwatch.Start();
            var isDirectory = entry.IsDirectory;
            var name = entry.FileName.ToString();
            var fullPath = entry.ToFullPath();
            var modified = entry.LastWriteTimeUtc.LocalDateTime;
            var created = extraColumnsEnabled ? entry.CreationTimeUtc.LocalDateTime : DateTime.MinValue;
            var accessed = extraColumnsEnabled ? entry.LastAccessTimeUtc.LocalDateTime : DateTime.MinValue;
            long? size = isDirectory ? null : entry.Length;
            var attributes = extraColumnsEnabled ? entry.Attributes : FileAttributes.None;
            metrics.MetadataStopwatch.Stop();

            metrics.DisplayStringStopwatch.Start();
            var kind = FileEntry.GetTypeText(name, isDirectory);
            var attributesText = extraColumnsEnabled ? FileEntry.GetAttributesText(attributes) : "";
            metrics.DisplayStringStopwatch.Stop();

            return new FileEntry
            {
                Name = name,
                FullPath = fullPath,
                Kind = kind,
                ModifiedAt = modified,
                CreatedAt = created,
                AccessedAt = accessed,
                Size = size,
                IsDirectory = isDirectory,
                AttributesText = attributesText
            };
        }
        finally
        {
            metrics.StopNested();
            metrics.CreateStopwatch.Stop();
        }
    }

    private static IEnumerable<FileEntry> EnumerateSortedEntries(
        string path,
        EnumerationOptions options,
        string sortColumn,
        bool sortAscending,
        bool sortFoldersFirst,
        bool extraColumnsEnabled,
        EnumerationMetrics metrics,
        CancellationToken cancellationToken)
    {
        var entries = new List<FileEntry>();
        foreach (var entry in EnumerateEntries(path, options, extraColumnsEnabled, metrics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(entry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        metrics.SortStopwatch.Start();
        var comparer = new FileEntryComparer(sortColumn, sortAscending, sortFoldersFirst);
        entries.Sort((left, right) =>
        {
            metrics.EntrySortKeyStopwatch.Start();
            try
            {
                return comparer.Compare(left, right);
            }
            finally
            {
                metrics.EntrySortKeyStopwatch.Stop();
            }
        });
        metrics.SortStopwatch.Stop();
        return entries;
    }

    private static bool CanSortByPath(string sortColumn)
    {
        return sortColumn is "Name" or "Kind" or "Extension" or "FullPath";
    }

    private static IEnumerable<FileSystemPath> EnumerateSortedPaths(
        string path,
        EnumerationOptions options,
        string sortColumn,
        bool sortAscending,
        bool sortFoldersFirst,
        bool extraColumnsEnabled,
        EnumerationMetrics metrics)
    {
        var enumerable = new FileSystemEnumerable<FileSystemPath>(
            path,
            (ref FileSystemEntry entry) => TransformPathEntry(ref entry, extraColumnsEnabled, metrics),
            options);
        var items = enumerable.ToList();

        var directories = items.Where(item => item.IsDirectory);
        var files = items.Where(item => !item.IsDirectory);

        return sortFoldersFirst
            ? SortPaths(directories, sortColumn, sortAscending, metrics).Concat(SortPaths(files, sortColumn, sortAscending, metrics))
            : SortPaths(items, sortColumn, sortAscending, metrics);
    }

    private static FileSystemPath TransformPathEntry(ref FileSystemEntry entry, bool extraColumnsEnabled, EnumerationMetrics metrics)
    {
        metrics.MetadataStopwatch.Start();
        try
        {
            var isDirectory = entry.IsDirectory;
            return new FileSystemPath(
                entry.ToFullPath(),
                entry.FileName.ToString(),
                isDirectory,
                entry.LastWriteTimeUtc.LocalDateTime,
                extraColumnsEnabled ? entry.CreationTimeUtc.LocalDateTime : DateTime.MinValue,
                extraColumnsEnabled ? entry.LastAccessTimeUtc.LocalDateTime : DateTime.MinValue,
                isDirectory ? (long?)null : entry.Length,
                extraColumnsEnabled ? entry.Attributes : FileAttributes.None);
        }
        finally
        {
            metrics.MetadataStopwatch.Stop();
        }
    }

    private static IEnumerable<FileSystemPath> SortPaths(IEnumerable<FileSystemPath> paths, string sortColumn, bool sortAscending, EnumerationMetrics metrics)
    {
        metrics.SortStopwatch.Start();
        try
        {
            var sorted = paths
                .OrderBy(path => GetPathSortText(path, sortColumn, metrics), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(path => path.Name, StringComparer.CurrentCultureIgnoreCase);

            return sortAscending
                ? sorted.ToList()
                : sorted.Reverse().ToList();
        }
        finally
        {
            metrics.SortStopwatch.Stop();
        }
    }

    private static string GetPathSortText(FileSystemPath path, string sortColumn, EnumerationMetrics metrics)
    {
        metrics.PathSortKeyStopwatch.Start();
        try
        {
            return sortColumn switch
            {
                "Kind" => path.IsDirectory
                    ? AppStrings.Get("TypeFolder")
                    : GetFileTypeText(path.Name),
                "Extension" => path.IsDirectory ? "" : Path.GetExtension(path.Name).TrimStart('.'),
                "FullPath" => path.Path,
                _ => path.Name
            };
        }
        finally
        {
            metrics.PathSortKeyStopwatch.Stop();
        }
    }

    private static string GetFileTypeText(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(extension)
            ? AppStrings.Get("TypeFile")
            : extension.TrimStart('.').ToUpperInvariant();
    }

    private sealed class EnumerationMetrics
    {
        public Stopwatch CreateStopwatch { get; } = new();

        public Stopwatch MetadataStopwatch { get; } = new();

        public Stopwatch DisplayStringStopwatch { get; } = new();

        public Stopwatch SortStopwatch { get; } = new();

        public Stopwatch PathSortKeyStopwatch { get; } = new();

        public Stopwatch EntrySortKeyStopwatch { get; } = new();

        public void StopNested()
        {
            if (MetadataStopwatch.IsRunning)
            {
                MetadataStopwatch.Stop();
            }

            if (DisplayStringStopwatch.IsRunning)
            {
                DisplayStringStopwatch.Stop();
            }
        }
    }

    private readonly record struct FileSystemPath(
        string Path,
        string Name,
        bool IsDirectory,
        DateTime Modified,
        DateTime Created,
        DateTime Accessed,
        long? Size,
        FileAttributes Attributes);
}
