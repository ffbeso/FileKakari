namespace FileKakari;

public sealed class UndoService
{
    private readonly FileOperationService _fileOperationService;
    private int _undoInProgress;
    public UndoAction? LastAction { get; private set; }

    public UndoService(FileOperationService fileOperationService) => _fileOperationService = fileOperationService;
    public void Clear() => LastAction = null;

    public void RecordRename(string originalPath, string currentPath, bool isDirectory)
    {
        if (LastAction is FileOperationUndoAction { Kind: UndoOperationKind.Create, Items.Count: 1 } createUndo
            && !createUndo.Items[0].PrimaryUndoCompleted
            && string.Equals(createUndo.Items[0].CurrentPath, originalPath, StringComparison.OrdinalIgnoreCase))
        {
            LastAction = createUndo with { Items = [createUndo.Items[0] with { CurrentPath = currentPath, IsDirectory = isDirectory }] };
            return;
        }
        LastAction = new RenameUndoAction(originalPath, currentPath, isDirectory);
    }

    public void RecordFileOperation(UndoOperationKind kind, IReadOnlyList<UndoFileOperationItem> items) =>
        LastAction = new FileOperationUndoAction(kind, items.ToArray());

    public async Task<IReadOnlyCollection<string>> UndoLastAsync()
    {
        if (Interlocked.CompareExchange(ref _undoInProgress, 1, 0) != 0) return [];
        try
        {
            var action = LastAction;
            if (action is null) return [];
            if (action is RenameUndoAction rename)
            {
                await _fileOperationService.RenameAsync(rename.CurrentPath, rename.OriginalPath, rename.IsDirectory);
                ReplacePending(action, null);
                return [rename.OriginalPath];
            }
            if (action is not FileOperationUndoAction operation)
                throw new InvalidOperationException(AppStrings.Get("UndoUnsupported"));

            var remaining = operation.Items.ToList();
            var restoredPaths = new List<string>();
            while (remaining.Count > 0)
            {
                var item = remaining[0];
                if (!item.PrimaryUndoCompleted)
                {
                    switch (operation.Kind)
                    {
                        case UndoOperationKind.Copy:
                        case UndoOperationKind.Create:
                            await _fileOperationService.DeleteAsync(item.CurrentPath, item.IsDirectory);
                            break;
                        case UndoOperationKind.Move:
                            await _fileOperationService.MoveAsync(item.CurrentPath, item.OriginalPath, item.IsDirectory);
                            break;
                        default:
                            throw new InvalidOperationException(AppStrings.Get("UndoUnsupported"));
                    }
                    remaining[0] = item = item with { PrimaryUndoCompleted = true };
                    var checkpoint = operation with { Items = remaining.ToArray() };
                    ReplacePending(action, checkpoint);
                    action = checkpoint;
                }

                // Keep the checkpoint if restoration fails; a retry must not repeat the delete/move.
                if (item.ReplacedPath is not null)
                    await _fileOperationService.MoveAsync(item.ReplacedPath, item.CurrentPath, item.ReplacedIsDirectory);
                if (operation.Kind == UndoOperationKind.Move) restoredPaths.Add(item.OriginalPath);
                remaining.RemoveAt(0);
                UndoAction? next = remaining.Count == 0 ? null : operation with { Items = remaining.ToArray() };
                ReplacePending(action, next);
                if (next is not null) action = next;
            }
            return restoredPaths;
        }
        finally { Interlocked.Exchange(ref _undoInProgress, 0); }
    }

    private void ReplacePending(UndoAction expected, UndoAction? next)
    {
        // Do not erase an operation recorded while this asynchronous undo was running.
        if (ReferenceEquals(LastAction, expected)) LastAction = next;
    }
}
