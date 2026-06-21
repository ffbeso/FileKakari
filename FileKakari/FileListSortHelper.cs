namespace FileKakari;

internal static class FileListSortHelper
{
    public static string GetSortPropertyName(string sortColumn)
    {
        var normalized = ColumnLayoutService.NormalizeColumnId(sortColumn);
        return normalized switch
        {
            "Size" => nameof(FileEntry.Size),
            "ModifiedAt" => nameof(FileEntry.ModifiedAt),
            "CreatedAt" => nameof(FileEntry.CreatedAt),
            "AccessedAt" => nameof(FileEntry.AccessedAt),
            "Extension" => nameof(FileEntry.Extension),
            "Attributes" => nameof(FileEntry.AttributesText),
            "FullPath" => nameof(FileEntry.FullPath),
            "ParentPath" => nameof(FileEntry.ParentPath),
            "BaseName" => nameof(FileEntry.BaseName),
            "Kind" => nameof(FileEntry.Kind),
            _ => nameof(FileEntry.Name)
        };
    }
}
