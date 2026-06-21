using System.Windows;

namespace FileKakari;

public sealed class RenameInteractionService
{
    private static readonly TimeSpan RenameClickDelay = TimeSpan.FromMilliseconds(540);
    private int _generation;

    public Point? PendingClickPoint { get; private set; }

    public FileEntry? PendingClickEntry { get; private set; }

    public bool IsCommitInProgress { get; private set; }

    public bool IsCanceling { get; private set; }

    public void SetPendingClick(FileEntry entry, Point point)
    {
        PendingClickEntry = entry;
        PendingClickPoint = point;
    }

    public void ClearPendingClick()
    {
        PendingClickEntry = null;
        PendingClickPoint = null;
    }

    public void CancelPendingClick()
    {
        ClearPendingClick();
        _generation++;
    }

    public int AdvanceGeneration()
    {
        return ++_generation;
    }

    public bool IsCurrentGeneration(int generation)
    {
        return generation == _generation;
    }

    public Task WaitForClickDelayAsync()
    {
        return Task.Delay(RenameClickDelay);
    }

    public bool HasActiveRename(IEnumerable<FileEntry> entries)
    {
        return IsCommitInProgress || entries.Any(entry => entry.IsRenaming);
    }

    public bool TryBeginCommit(FileEntry entry)
    {
        if (IsCommitInProgress || IsCanceling || !entry.IsRenaming)
        {
            return false;
        }

        IsCommitInProgress = true;
        return true;
    }

    public void EndCommit()
    {
        IsCommitInProgress = false;
    }

    public void BeginCancel()
    {
        IsCanceling = true;
    }

    public void EndCancel()
    {
        IsCanceling = false;
    }
}
