namespace FileKakari;

public sealed class FileService
{
    private readonly IFileEnumerator _fileEnumerator;

    public FileService(IFileEnumerator fileEnumerator)
    {
        _fileEnumerator = fileEnumerator;
    }

    public IEnumerable<FileEntry> Enumerate(string path, CancellationToken cancellationToken)
    {
        return _fileEnumerator.Enumerate(path, cancellationToken);
    }

    public IEnumerable<FileEntry> Enumerate(
        string path,
        string sortColumn,
        bool sortAscending,
        bool sortFoldersFirst,
        bool extraColumnsEnabled,
        CancellationToken cancellationToken)
    {
        return _fileEnumerator.Enumerate(path, sortColumn, sortAscending, sortFoldersFirst, extraColumnsEnabled, cancellationToken);
    }
}
