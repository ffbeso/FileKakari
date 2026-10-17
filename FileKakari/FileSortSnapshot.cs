using System.Globalization;

namespace FileKakari;

// Values are copied on the UI thread. Workers never inspect live FileEntry objects.
public sealed record FileSortKey(int SourceIndex, bool IsDirectory, string Name, string FullPath,
    string Text, long? Size, DateTime Date, int? PreviousIndex);

public static class FileSortSnapshot
{
    private static readonly SemaphoreSlim Workers = new(2);

    public static FileSortKey Capture(FileEntry entry, int index, string column, int? previousIndex)
    {
        column = ColumnLayoutService.NormalizeColumnId(column);
        var text = column switch
        {
            "Kind" => entry.Kind, "Extension" => entry.Extension, "Attributes" => entry.AttributesText,
            "FullPath" => entry.FullPath, "ParentPath" => entry.ParentPath, "BaseName" => entry.BaseName,
            _ => entry.Name
        };
        var date = column switch
        {
            "CreatedAt" => entry.CreatedAt, "AccessedAt" => entry.AccessedAt, _ => entry.ModifiedAt
        };
        return new(index, entry.IsDirectory, entry.Name, entry.FullPath, text, entry.Size, date, previousIndex);
    }

    public static async Task<int[]> SortAsync(FileSortKey[] keys, string column, bool ascending,
        bool foldersFirst, CancellationToken cancellationToken)
    {
        column = ColumnLayoutService.NormalizeColumnId(column);
        var compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        await Workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Sort(keys, column, ascending, foldersFirst, compareInfo, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { Workers.Release(); }
    }

    private static int[] Sort(FileSortKey[] keys, string column, bool ascending, bool foldersFirst,
        CompareInfo culture, CancellationToken token)
    {
        int Text(string left, string right) => culture.Compare(left, right, CompareOptions.IgnoreCase);
        int Compare(FileSortKey left, FileSortKey right)
        {
            if (foldersFirst && left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;
            var result = column switch
            {
                "Size" => Nullable.Compare(left.Size, right.Size),
                "ModifiedAt" or "CreatedAt" or "AccessedAt" => DateTime.Compare(left.Date, right.Date),
                _ => Text(left.Text, right.Text)
            };
            if (result != 0) return ascending ? result : -result;
            if (left.PreviousIndex is { } li && right.PreviousIndex is { } ri) return li.CompareTo(ri);
            if (column != "Name") result = Text(left.Name, right.Name);
            if (result == 0) result = Text(left.FullPath, right.FullPath);
            return ascending ? result : -result;
        }

        token.ThrowIfCancellationRequested();
        // Stable merge sort checks cancellation inside each merge, including the final large one.
        var order = Enumerable.Range(0, keys.Length).ToArray();
        var buffer = new int[keys.Length];
        for (long width = 1; width < keys.Length; width *= 2)
        {
            for (long start = 0; start < keys.Length; start += width * 2)
            {
                int left = (int)start, middle = (int)Math.Min(start + width, keys.Length);
                int right = middle, end = (int)Math.Min(start + width * 2, keys.Length);
                for (var output = left; output < end; output++)
                {
                    if ((output & 255) == 0) token.ThrowIfCancellationRequested();
                    buffer[output] = left < middle && (right >= end || Compare(keys[order[left]], keys[order[right]]) <= 0)
                        ? order[left++] : order[right++];
                }
            }
            (order, buffer) = (buffer, order);
        }
        for (var i = 0; i < order.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            order[i] = keys[order[i]].SourceIndex;
        }
        token.ThrowIfCancellationRequested();
        return order;
    }
}
