namespace FileKakari;

public sealed class UndoService
{
    private readonly FileOperationService _fileOperationService;

    public UndoAction? LastAction { get; private set; }

    public UndoService(FileOperationService fileOperationService)
    {
        _fileOperationService = fileOperationService;
    }

    public void Clear()
    {
        LastAction = null;
    }

    public void RecordRename(string originalPath, string currentPath, bool isDirectory)
    {
        if (LastAction is FileOperationUndoAction { Kind: UndoOperationKind.Create, Items.Count: 1 } createUndo
            && string.Equals(createUndo.Items[0].CurrentPath, originalPath, StringComparison.OrdinalIgnoreCase))
        {
            LastAction = createUndo with
            {
                Items = [createUndo.Items[0] with { CurrentPath = currentPath, IsDirectory = isDirectory }]
            };
            return;
        }

        LastAction = new RenameUndoAction(originalPath, currentPath, isDirectory);
    }

    public void RecordFileOperation(UndoOperationKind kind, IReadOnlyList<UndoFileOperationItem> items)
    {
        LastAction = new FileOperationUndoAction(kind, items);
    }

    public async Task<IReadOnlyCollection<string>> UndoLastAsync()
    {
        if (LastAction is null)
        {
            return [];
        }

        var undoAction = LastAction;
        var pathsToSelect = await ExecuteUndoAsync(undoAction);
        LastAction = null;
        return pathsToSelect;
    }

    private async Task<IReadOnlyCollection<string>> ExecuteUndoAsync(UndoAction undoAction)
    {
        switch (undoAction)
        {
            case RenameUndoAction rename:
                await _fileOperationService.MoveAsync(rename.CurrentPath, rename.OriginalPath, rename.IsDirectory);
                return [rename.OriginalPath];
            case FileOperationUndoAction { Kind: UndoOperationKind.Create } create:
                foreach (var item in create.Items)
                {
                    await _fileOperationService.DeleteAsync(item.CurrentPath, item.IsDirectory);
                    await RestoreReplacementAsync(item);
                }
                return [];
            case FileOperationUndoAction { Kind: UndoOperationKind.Copy } copy:
                foreach (var item in copy.Items)
                {
                    await _fileOperationService.DeleteAsync(item.CurrentPath, item.IsDirectory);
                    await RestoreReplacementAsync(item);
                }
                return [];
            case FileOperationUndoAction { Kind: UndoOperationKind.Move } move:
                foreach (var item in move.Items)
                {
                    await _fileOperationService.MoveAsync(item.CurrentPath, item.OriginalPath, item.IsDirectory);
                    await RestoreReplacementAsync(item);
                }
                return move.Items.Select(item => item.OriginalPath).ToList();
            default:
                throw new InvalidOperationException(AppStrings.Get("UndoUnsupported"));
        }
    }

    private async Task RestoreReplacementAsync(UndoFileOperationItem item)
    {
        if (item.ReplacedPath is null)
        {
            return;
        }

        if (item.ReplacedIsDirectory)
        {
            if (!System.IO.Directory.Exists(item.ReplacedPath))
            {
                return;
            }
        }
        else
        {
            if (!System.IO.File.Exists(item.ReplacedPath))
            {
                return;
            }
        }

        try
        {
            await _fileOperationService.MoveAsync(item.ReplacedPath, item.CurrentPath, item.ReplacedIsDirectory);
        }
        catch
        {
            // Ignore failure to restore so the rest of the undo actions can proceed
        }
    }
}
