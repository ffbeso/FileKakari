namespace FileKakari;

public interface IFileEnumerator
{
    IEnumerable<FileEntry> Enumerate(string path, CancellationToken cancellationToken);

    IEnumerable<FileEntry> Enumerate(
        string path,
        string sortColumn,
        bool sortAscending,
        bool sortFoldersFirst,
        bool extraColumnsEnabled,
        CancellationToken cancellationToken);
}
