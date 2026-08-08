using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FileKakari;

public static class FileNameNormalizer
{
    private static readonly Regex TagRegex = new(
        @"\[(字|再|新|終|初|解|デ|二|多|吹|手|SS|N|映|劇|生|HV|4K|8K)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches: YYYYMMDD_hhmmss, YYYY-MM-DD / YYYYMMDD, 6-digit year-month (20YYMM like 202608 for monthly shows), and YYYY年MM月DD日
    private static readonly Regex DateTimeRegex = new(
        @"\b\d{8}_\d{4,6}\b|\b\d{4}[-._/]?\d{2}[-._/]?\d{2}\b|\b20\d{2}(0[1-9]|1[0-2])\b|\b\d{4}年\d{1,2}月\d{1,2}日\b",
        RegexOptions.Compiled);

    private static readonly Regex EpisodeRegex = new(
        @"(第\s*\d+\s*[話回]|\#\s*\d+|＃\s*\d+|\b[Ee][Pp]?\s*\d+\b)",
        RegexOptions.Compiled);

    private static readonly Regex TrimCharsRegex = new(
        @"^[\s_・\-]+|[\s_・\-]+$",
        RegexOptions.Compiled);

    // Cache display name to normalized key mapping for consistency
    private static readonly ConcurrentDictionary<string, string> KeyToDisplayCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a file name string into a group title.
    /// Removal Rules (Phase 2-A):
    /// 1. Recording attribute tags: [字], [再], [新], [終], etc.
    /// 2. Date/Datetime: YYYY-MM-DD / YYYYMMDD, YYYYMMDD_hhmmss, YYYY年MM月DD日, 6-digit year-month (20YYMM)
    /// 3. Explicit episode numbers: 第XX話, 第XX回, #XX, ＃XX, EPXX, EXX
    /// 4. Trim separators: space, _, -, ・
    /// </summary>
    public static string Normalize(string rawFileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(rawFileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return rawFileName;
        }

        string cleaned = baseName;

        // 1. Remove recording attribute tags
        cleaned = TagRegex.Replace(cleaned, "");

        // 2. Remove date and datetime
        cleaned = DateTimeRegex.Replace(cleaned, "");

        // 3. Remove explicit episode numbers
        cleaned = EpisodeRegex.Replace(cleaned, "");

        // 4. Trim leading/trailing separators
        cleaned = TrimCharsRegex.Replace(cleaned, "").Trim();

        // 5. Fallback if empty or whitespace
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = baseName;
        }

        // Cache first encountered clean display name for normalized comparison key
        string compareKey = BuildCompareKey(cleaned);
        return KeyToDisplayCache.GetOrAdd(compareKey, _ => cleaned);
    }

    public static string BuildCompareKey(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            // Normalize full-width alphanumeric to half-width
            if (c >= '０' && c <= '９')
            {
                sb.Append((char)(c - '０' + '0'));
            }
            else if (c >= 'Ａ' && c <= 'Ｚ')
            {
                sb.Append((char)(c - 'Ａ' + 'a'));
            }
            else if (c >= 'ａ' && c <= 'ｚ')
            {
                sb.Append((char)(c - 'ａ' + 'a'));
            }
            else if (c == '　')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Trim();
    }
}
