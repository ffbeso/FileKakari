using System;
using System.Collections.Generic;
using System.Linq;

namespace FileKakari;

public sealed class SimilarNameGroupIndex
{
    private static readonly char[] StrongDelimiters = new[]
    {
        ' ', '　', '-', '_', '・',
        '「', '」', '『', '』',
        '(', ')', '（', '）',
        '[', ']', '［', '］',
        '【', '】',
        '“', '”', '"'
    };

    private static readonly char[] ConditionalDelimiters = new[]
    {
        '！', '!', '▽', '▼', '★', '☆', '◆', '◇', '■', '□', '♪'
    };

    private static readonly char[] AllDelimiters = StrongDelimiters.Concat(ConditionalDelimiters).ToArray();

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
            string candidate = ExtractCandidateToken(item.P2A);
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

    private static string ExtractCandidateToken(string p2a)
    {
        int startIdx = 0;
        while (startIdx < p2a.Length && Array.IndexOf(AllDelimiters, p2a[startIdx]) >= 0)
        {
            startIdx++;
        }

        if (startIdx >= p2a.Length)
        {
            return p2a;
        }

        int curr = startIdx;
        while (curr < p2a.Length)
        {
            int nextIdx = p2a.IndexOfAny(AllDelimiters, curr);
            if (nextIdx < 0)
            {
                break;
            }

            char delim = p2a[nextIdx];
            if (Array.IndexOf(StrongDelimiters, delim) >= 0)
            {
                if (TryEvaluateToken(p2a, startIdx, nextIdx, out string candidate))
                {
                    return candidate;
                }
                curr = nextIdx + 1;
            }
            else if (Array.IndexOf(ConditionalDelimiters, delim) >= 0)
            {
                string prefixRaw = p2a.Substring(startIdx, nextIdx - startIdx).Trim(AllDelimiters);
                string compareKey = FileNameNormalizer.BuildCompareKey(prefixRaw).Trim();
                if (compareKey.Length >= 7)
                {
                    if (TryEvaluateToken(p2a, startIdx, nextIdx, out string candidate))
                    {
                        return candidate;
                    }
                }
                curr = nextIdx + 1;
            }
            else
            {
                curr = nextIdx + 1;
            }
        }

        if (startIdx > 0)
        {
            string token = p2a.Substring(startIdx).Trim(AllDelimiters);
            return token.Length > 2 ? token : p2a;
        }

        return p2a;
    }

    private static bool TryEvaluateToken(string p2a, int startIdx, int nextIdx, out string candidate)
    {
        candidate = p2a;
        string firstToken = p2a.Substring(startIdx, nextIdx - startIdx).Trim(AllDelimiters);
        if (IsEnglishArticle(firstToken))
        {
            int secondIdx = p2a.IndexOfAny(AllDelimiters, nextIdx + 1);
            if (secondIdx > nextIdx)
            {
                string extendedToken = p2a.Substring(startIdx, secondIdx - startIdx).Trim(AllDelimiters);
                if (extendedToken.Length > 2)
                {
                    candidate = extendedToken;
                    return true;
                }
            }
            else
            {
                string extendedToken = p2a.Substring(startIdx).Trim(AllDelimiters);
                if (extendedToken.Length > 2)
                {
                    candidate = extendedToken;
                    return true;
                }
            }
            return false;
        }
        else
        {
            if (firstToken.Length > 2)
            {
                candidate = firstToken;
                return true;
            }
            return false;
        }
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

    private static bool IsEnglishArticle(string text)
    {
        return string.Equals(text, "THE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "A", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "AN", StringComparison.OrdinalIgnoreCase);
    }
}
