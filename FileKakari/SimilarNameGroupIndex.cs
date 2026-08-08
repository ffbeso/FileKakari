using System;
using System.Collections.Generic;
using System.Linq;

namespace FileKakari;

public sealed class SimilarNameGroupIndex
{
    private static readonly char[] Delimiters = new[] { ' ', '　', '-', '_', '・', '「', '『', '[', '(', '【' };

    private readonly Dictionary<string, string> _pathToGroupKey = new(StringComparer.OrdinalIgnoreCase);

    public long LastBuildElapsedMs { get; private set; }

    public static SimilarNameGroupIndex Build(IEnumerable<FileEntry> entries)
    {
        var index = new SimilarNameGroupIndex();
        index.Rebuild(entries);
        return index;
    }

    public void Rebuild(IEnumerable<FileEntry> entries)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _pathToGroupKey.Clear();

        var entryList = entries.ToList();
        if (entryList.Count == 0)
        {
            return;
        }

        // 1. Compute Phase 2-A names for files
        var fileItems = new List<(FileEntry Entry, string P2A)>();
        foreach (var entry in entryList)
        {
            if (entry.IsDirectory)
            {
                _pathToGroupKey[entry.FullPath] = "フォルダ";
            }
            else
            {
                string p2a = FileNameNormalizer.Normalize(entry.Name);
                fileItems.Add((entry, p2a));
            }
        }

        // 2. Extract TokenPrefix candidates
        var rawToCandidate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in fileItems)
        {
            string p2a = item.P2A;
            int firstIdx = p2a.IndexOfAny(Delimiters);
            string candidate;

            if (firstIdx > 2)
            {
                candidate = p2a.Substring(0, firstIdx).Trim();
            }
            else
            {
                candidate = p2a;
            }

            rawToCandidate[item.Entry.FullPath] = candidate;
            candidateCounts[candidate] = candidateCounts.TryGetValue(candidate, out int count) ? count + 1 : 1;
        }

        // 3. Final key assignment (Require >= 2 items for TokenPrefix candidate, else fallback to P2A)
        foreach (var item in fileItems)
        {
            string candidate = rawToCandidate[item.Entry.FullPath];
            if (candidateCounts.TryGetValue(candidate, out int count) && count >= 2)
            {
                _pathToGroupKey[item.Entry.FullPath] = candidate;
            }
            else
            {
                _pathToGroupKey[item.Entry.FullPath] = item.P2A;
            }
        }

        sw.Stop();
        LastBuildElapsedMs = sw.ElapsedMilliseconds;
    }

    public string GetGroupKey(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            return "フォルダ";
        }

        if (_pathToGroupKey.TryGetValue(entry.FullPath, out string? key))
        {
            return key;
        }

        return FileNameNormalizer.Normalize(entry.Name);
    }
}
