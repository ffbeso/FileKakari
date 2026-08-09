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

        var grouped = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string key = FileGroupHelper.GetGroupKey(entry, mode, groupIndex);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<FileEntry>();
                grouped[key] = list;
            }
            list.Add(entry);
        }

        foreach (var (key, items) in grouped)
        {
            if (items.Count == 0) continue;

            FileEntry representative = items[0];
            for (int i = 1; i < items.Count; i++)
            {
                var candidate = items[i];
                int cmp = CompareByColumn(candidate, representative, normalizedColumn, sortFoldersFirst);
                if (sortAscending ? cmp < 0 : cmp > 0)
                {
                    representative = candidate;
                }
            }
            index._groupToRepresentative[key] = representative;
        }

        sw.Stop();
        index.LastBuildElapsedMs = sw.ElapsedMilliseconds;
        return index;
    }

    public FileEntry? GetRepresentative(string groupKey)
    {
        return _groupToRepresentative.TryGetValue(groupKey, out var rep) ? rep : null;
    }

    private static int CompareByColumn(FileEntry left, FileEntry right, string columnId, bool foldersFirst)
    {
        if (foldersFirst && left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return columnId switch
        {
            "Name" => string.Compare(FileNameNormalizer.BuildCompareKey(left.Name), FileNameNormalizer.BuildCompareKey(right.Name), StringComparison.OrdinalIgnoreCase),
            "Size" => left.Size < right.Size ? -1 : (left.Size > right.Size ? 1 : 0),
            "ModifiedAt" => DateTime.Compare(left.ModifiedAt, right.ModifiedAt),
            "CreatedAt" => DateTime.Compare(left.CreatedAt, right.CreatedAt),
            "AccessedAt" => DateTime.Compare(left.AccessedAt, right.AccessedAt),
            "Kind" => string.Compare(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(FileNameNormalizer.BuildCompareKey(left.Name), FileNameNormalizer.BuildCompareKey(right.Name), StringComparison.OrdinalIgnoreCase)
        };
    }
}
