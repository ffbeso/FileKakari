using System.Collections;
using System.Collections.Generic;

namespace FileKakari;

public sealed class FileEntryComparer : IComparer
{
    private readonly string _columnId;
    private readonly bool _ascending;
    private readonly bool _foldersFirst;
    private readonly Dictionary<string, int>? _currentOrder;

    public FileEntryComparer(
        string columnId,
        bool ascending,
        bool foldersFirst,
        Dictionary<string, int>? currentOrder = null)
    {
        _columnId = ColumnLayoutService.NormalizeColumnId(columnId);
        _ascending = ascending;
        _foldersFirst = foldersFirst;
        _currentOrder = currentOrder;
    }

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is not FileEntry left)
        {
            return _ascending ? -1 : 1;
        }

        if (y is not FileEntry right)
        {
            return _ascending ? 1 : -1;
        }

        if (_foldersFirst && left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        var result = CompareByColumn(left, right);
        if (result == 0)
        {
            if (_currentOrder is not null
                && _currentOrder.TryGetValue(left.FullPath, out var leftIndex)
                && _currentOrder.TryGetValue(right.FullPath, out var rightIndex))
            {
                return leftIndex.CompareTo(rightIndex);
            }

            if (!string.Equals(_columnId, "Name", StringComparison.Ordinal))
            {
                result = CompareText(left.Name, right.Name);
            }
        }

        return _ascending ? result : -result;
    }

    private int CompareByColumn(FileEntry left, FileEntry right)
    {
        return _columnId switch
        {
            "Kind" => CompareText(left.Kind, right.Kind),
            "Size" => CompareNullableLong(left.Size, right.Size),
            "ModifiedAt" => DateTime.Compare(left.ModifiedAt, right.ModifiedAt),
            "CreatedAt" => DateTime.Compare(left.CreatedAt, right.CreatedAt),
            "AccessedAt" => DateTime.Compare(left.AccessedAt, right.AccessedAt),
            "Extension" => CompareText(left.Extension, right.Extension),
            "Attributes" => CompareText(left.AttributesText, right.AttributesText),
            "FullPath" => CompareText(left.FullPath, right.FullPath),
            "ParentPath" => CompareText(left.ParentPath, right.ParentPath),
            "BaseName" => CompareText(left.BaseName, right.BaseName),
            _ => CompareText(left.Name, right.Name)
        };
    }

    private static int CompareText(string left, string right)
    {
        return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
    }

    private static int CompareNullableLong(long? left, long? right)
    {
        return (left, right) switch
        {
            (long leftValue, long rightValue) => leftValue.CompareTo(rightValue),
            (not null, null) => 1,
            (null, not null) => -1,
            _ => 0
        };
    }
}
