using System.ComponentModel;
using System.IO;

namespace FileKakari;

public sealed class FolderTab : INotifyPropertyChanged
{
    public FolderTab(
        string path,
        string? id = null,
        FileDisplayMode viewMode = FileDisplayMode.Details,
        WorkspaceTabState? state = null)
    {
        State = state ?? new WorkspaceTabState(path, id, viewMode);
        State.PropertyChanged += OnStatePropertyChanged;
        Navigation = new NavigationState(path);
        if (state is null)
        {
            ViewMode = AppSettings.NormalizeDisplayMode(viewMode);
        }
        else if (string.IsNullOrWhiteSpace(State.CurrentPath))
        {
            State.CurrentPath = path;
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(WorkspaceTabState.IsPathMissing), StringComparison.Ordinal))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPathMissing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubTabToolTip)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NavigationState Navigation { get; }

    public WorkspaceTabState State { get; }

    public string Id => State.Id;

    public string FilterText
    {
        get => State.FilterText;
        set => State.FilterText = value;
    }

    public string SortColumn
    {
        get => State.SortColumn;
        set => State.SortColumn = value;
    }

    public bool SortAscending
    {
        get => State.SortAscending;
        set => State.SortAscending = value;
    }

    public GroupMode GroupMode
    {
        get => State.GroupMode;
        set => State.GroupMode = value;
    }

    public FileDisplayMode ViewMode
    {
        get => State.ViewMode;
        set => State.ViewMode = AppSettings.NormalizeDisplayMode(value);
    }

    public string? CachedPath => State.CachedPath;

    public IReadOnlyList<FileEntry>? CachedItems => State.CachedItems;

    public DateTimeOffset? LastLoadedAt => State.LastLoadedAt;

    public bool HasPendingExternalChange => State.HasPendingExternalChange;

    public DateTimeOffset? LastExternalChangeAt => State.LastExternalChangeAt;

    public double VerticalOffset
    {
        get => State.VerticalOffset;
        set => State.VerticalOffset = value;
    }

    public IReadOnlyList<string> SelectedPaths
    {
        get => State.SelectedPaths;
        set => State.SelectedPaths = value;
    }

    public bool IsDisconnected { get; private set; }

    public bool IsPathMissing => State.IsPathMissing;

    public string SubTabToolTip =>
        IsPathMissing
            ? AppStrings.Format("PathNotFound", Navigation.CurrentPath)
            : Navigation.CurrentPath;

    public bool IsFolderLocked { get; private set; }

    public string? HeaderOverride { get; private set; }

    public bool HasCachedItemsForCurrentPath =>
        CachedItems is not null
        && string.Equals(CachedPath, Navigation.CurrentPath, StringComparison.OrdinalIgnoreCase);

    public string Header
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(HeaderOverride))
            {
                return HeaderOverride;
            }

            if (SpecialLocationService.IsSpecialUri(Navigation.CurrentPath))
            {
                return AppStrings.Get("LocationThisPc");
            }

            var path = Navigation.CurrentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? Navigation.CurrentPath : name;
        }
    }

    public void RefreshHeader()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubTabToolTip)));
    }

    public void SetPathMissing(bool isMissing)
    {
        State.SetPathMissing(isMissing);
    }

    public void SetHeaderOverride(string? header)
    {
        HeaderOverride = string.IsNullOrWhiteSpace(header) ? null : header;
        RefreshHeader();
    }

    public void StoreItems(string path, IReadOnlyList<FileEntry> items)
    {
        State.StoreItems(path, items);
    }

    public void MarkPendingExternalChange()
    {
        State.MarkPendingExternalChange();
    }

    public void ClearPendingExternalChange()
    {
        State.ClearPendingExternalChange();
    }

    public void MarkDisconnected()
    {
        IsDisconnected = true;
    }

    public void ClearDisconnected()
    {
        IsDisconnected = false;
    }

    public void SetFolderLocked(bool isLocked)
    {
        if (IsFolderLocked == isLocked)
        {
            return;
        }

        IsFolderLocked = isLocked;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFolderLocked)));
        RefreshHeader();
    }

    public void ClearItems()
    {
        State.ClearItems();
        State.ClearPendingExternalChange();
        IsDisconnected = false;
        IsFolderLocked = false;
    }
}
