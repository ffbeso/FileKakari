namespace FileKakari;

public abstract record UndoAction;

public sealed record RenameUndoAction(string OriginalPath, string CurrentPath, bool IsDirectory) : UndoAction;

public sealed record FileOperationUndoAction(UndoOperationKind Kind, IReadOnlyList<UndoFileOperationItem> Items) : UndoAction;

public sealed record UndoFileOperationItem(
    string OriginalPath,
    string CurrentPath,
    bool IsDirectory,
    string? ReplacedPath = null,
    bool ReplacedIsDirectory = false);

public enum UndoOperationKind
{
    Create,
    Copy,
    Move
}
