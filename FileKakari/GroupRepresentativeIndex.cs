using System;
using System.Collections.Generic;
using System.Linq;

namespace FileKakari;

public sealed class GroupRepresentativeIndex
{
    private readonly Dictionary<string, FileEntry> _groupToRepresentative = new(StringComparer.OrdinalIgnoreCase);

    public long LastBuildElapsedMs { get; private set; }

    public static GroupRepresentativeIndex Build(
        IEnumerable<FileEntry> entries,
        GroupMode mode,
        string sortColumn,
        bool sortAscending,
        bool sortFoldersFirst,
        SimilarNameGroupIndex? groupIndex = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = new GroupRepresentativeIndex();

        if (mode == GroupMode.None)
        {
            sw.Stop();
            index.LastBuildElapsedMs = sw.ElapsedMilliseconds;
            return index;
        }

        string normalizedColumn = ColumnLayoutService.NormalizeColumnId(sortColumn);

        var useNameKey = normalizedColumn is not ("Size" or "ModifiedAt" or "CreatedAt" or "AccessedAt" or "Kind");
        var representativeNameKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string key = FileGroupHelper.GetGroupKey(entry, mode, groupIndex);
            var nameKey = useNameKey ? FileNameNormalizer.BuildCompareKey(entry.Name) : "";
            if (!index._groupToRepresentative.TryGetValue(key, out var representative))
            {
                index._groupToRepresentative[key] = entry;
                if (useNameKey) representativeNameKeys[key] = nameKey;
                continue;
            }
            var cmp = CompareByColumn(entry, representative, normalizedColumn, sortFoldersFirst,
                nameKey, useNameKey ? representativeNameKeys[key] : "");
            if (sortAscending ? cmp < 0 : cmp > 0)
            {
                index._groupToRepresentative[key] = entry;
                if (useNameKey) representativeNameKeys[key] = nameKey;
            }
        }

        sw.Stop();
        index.LastBuildElapsedMs = sw.ElapsedMilliseconds;
        return index;
    }

    public FileEntry? GetRepresentative(string groupKey)
    {
        return _groupToRepresentative.TryGetValue(groupKey, out var rep) ? rep : null;
    }

    private static int CompareByColumn(FileEntry left, FileEntry right, string columnId, bool foldersFirst, string leftNameKey, string rightNameKey)
    {
        if (foldersFirst && left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return columnId switch
        {
            "Name" => string.Compare(leftNameKey, rightNameKey, StringComparison.OrdinalIgnoreCase),
            "Size" => left.Size < right.Size ? -1 : (left.Size > right.Size ? 1 : 0),
            "ModifiedAt" => DateTime.Compare(left.ModifiedAt, right.ModifiedAt),
            "CreatedAt" => DateTime.Compare(left.CreatedAt, right.CreatedAt),
            "AccessedAt" => DateTime.Compare(left.AccessedAt, right.AccessedAt),
            "Kind" => string.Compare(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(leftNameKey, rightNameKey, StringComparison.OrdinalIgnoreCase)
        };
    }
}
