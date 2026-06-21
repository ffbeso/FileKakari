using System.Collections.ObjectModel;

namespace FileKakari;

public sealed class WorkspaceSessionFolderTabSync
{
    private readonly ObservableCollection<FolderTab> _displayTabs;

    public WorkspaceSessionFolderTabSync(ObservableCollection<FolderTab> displayTabs)
    {
        _displayTabs = displayTabs;
    }

    public bool IsApplying { get; private set; }

    public void ApplyToDisplay(WorkspaceSession session)
    {
        IsApplying = true;
        try
        {
            _displayTabs.Clear();
            var primaryPane = session.PaneGroups.FirstOrDefault(p => string.Equals(p.Id, "primary", StringComparison.OrdinalIgnoreCase)) ?? session.PaneGroups.FirstOrDefault();
            if (primaryPane is not null)
            {
                var activeTab = primaryPane.ActiveTab ?? primaryPane.Tabs.FirstOrDefault();
                if (activeTab is not null)
                {
                    _displayTabs.Add(activeTab);
                }
            }
        }
        finally
        {
            IsApplying = false;
        }
    }

    public void CaptureFromDisplay(WorkspaceSession session, int selectedDisplayIndex)
    {
        if (IsApplying)
        {
            return;
        }

        session.SelectedTabIndex = 0;
    }

    public int GetDisplaySelectedIndex(WorkspaceSession session)
    {
        return 0;
    }

    private static void ReplaceTabs(ObservableCollection<FolderTab> target, IEnumerable<FolderTab> source)
    {
        var tabs = source.ToList();
        target.Clear();
        foreach (var tab in tabs)
        {
            target.Add(tab);
        }
    }
}
