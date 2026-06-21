using System.ComponentModel;

namespace FileKakari;

public enum MainTabKind
{
    Workspace,
    InternalPage
}

public enum InternalPageKind
{
    Settings,
    Help,
    LogViewer,
    About,
    ThemeEditor,
    UserCommandEditor
}

public sealed class MainTabItem : INotifyPropertyChanged, IDisposable
{
    private string _title;
    private object? _content;

    private MainTabItem(
        MainTabKind kind,
        string title,
        WorkspaceSession? workspaceSession,
        InternalPageKind? internalPageKind,
        object? content)
    {
        Id = Guid.NewGuid().ToString("N");
        Kind = kind;
        _title = title;
        WorkspaceSession = workspaceSession;
        InternalPageKind = internalPageKind;
        _content = content;

        if (WorkspaceSession is not null)
        {
            WorkspaceSession.PropertyChanged += WorkspaceSession_PropertyChanged;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public MainTabKind Kind { get; }

    public WorkspaceSession? WorkspaceSession { get; }

    public InternalPageKind? InternalPageKind { get; }

    public bool IsWorkspaceSessionTab => Kind == MainTabKind.Workspace;

    public bool IsWorkspace => WorkspaceSession?.IsWorkspace == true;

    public bool IsInternalPage => Kind == MainTabKind.InternalPage;

    public string InternalPageIconGlyph => InternalPageKind switch
    {
        FileKakari.InternalPageKind.Settings => "\uE713",
        FileKakari.InternalPageKind.LogViewer => "\uE9D9",
        FileKakari.InternalPageKind.UserCommandEditor => "\uE70C",
        _ => "\uE8A5"
    };

    public string Header => Title;

    public bool IsFolderLocked => WorkspaceSession?.IsFolderLocked == true;

    public bool IsRenaming
    {
        get => WorkspaceSession?.IsRenaming == true;
        set
        {
            if (WorkspaceSession is not null)
            {
                WorkspaceSession.IsRenaming = value;
            }
        }
    }

    public string RenameText
    {
        get => WorkspaceSession?.RenameText ?? _title;
        set
        {
            if (WorkspaceSession is not null)
            {
                WorkspaceSession.RenameText = value;
            }
            else
            {
                Title = value;
            }
        }
    }

    public string Title
    {
        get => WorkspaceSession?.Header ?? _title;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (string.Equals(_title, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _title = normalized;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Header));
        }
    }

    public object? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
            {
                return;
            }

            _content = value;
            OnPropertyChanged(nameof(Content));
        }
    }

    public static MainTabItem FromWorkspace(WorkspaceSession workspaceSession)
    {
        ArgumentNullException.ThrowIfNull(workspaceSession);
        return new MainTabItem(
            MainTabKind.Workspace,
            workspaceSession.Header,
            workspaceSession,
            internalPageKind: null,
            content: null);
    }

    public static MainTabItem CreateInternalPage(
        InternalPageKind pageKind,
        string title,
        object? content = null)
    {
        return new MainTabItem(
            MainTabKind.InternalPage,
            title,
            workspaceSession: null,
            pageKind,
            content);
    }

    public void Dispose()
    {
        if (WorkspaceSession is not null)
        {
            WorkspaceSession.PropertyChanged -= WorkspaceSession_PropertyChanged;
        }
    }

    private void WorkspaceSession_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceSession.Header))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Header));
        }
        else if (e.PropertyName == nameof(WorkspaceSession.IsFolderLocked))
        {
            OnPropertyChanged(nameof(IsFolderLocked));
        }
        else if (e.PropertyName == nameof(WorkspaceSession.IsRenaming))
        {
            OnPropertyChanged(nameof(IsRenaming));
        }
        else if (e.PropertyName == nameof(WorkspaceSession.RenameText))
        {
            OnPropertyChanged(nameof(RenameText));
        }
        else if (e.PropertyName == nameof(WorkspaceSession.IsWorkspace))
        {
            OnPropertyChanged(nameof(IsWorkspace));
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
