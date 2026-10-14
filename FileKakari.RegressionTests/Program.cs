using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileKakari;

// Service checks only: never construct App/MainWindow or load/save the user's profile.
if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out _))
{
    Console.Error.WriteLine("Usage: FileKakari.RegressionTests <run-directory> <run-id>");
    return 2;
}

var runDirectory = Path.GetFullPath(args[0]);
var checks = new List<CheckResult>();
try
{
    Directory.CreateDirectory(runDirectory);
    var fixtures = Path.Combine(runDirectory, "fixtures");
    if (Directory.Exists(fixtures)) throw new IOException("Fixture directory already exists. Use a fresh run directory.");
    Directory.CreateDirectory(fixtures);
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    AppStrings.Configure(AppLanguageMode.Japanese);
    var textProvider = new BuiltInTextPreviewProvider();

    await CheckAsync("pane-viewstate-capture-isolation-and-transient-guard", () => RunOnStaAsync(() =>
    {
        var left = new FileListState();
        var right = new FileListState();
        left.ReplaceItems(fixtures, [], loadedStateId: "left-tab");
        right.ReplaceItems(fixtures, [], loadedStateId: "right-tab");
        using (left.ViewStateSyncSuppression.Enter())
        {
            Require(!left.CanCaptureViewState("left-tab", fixtures), "Partial restore can overwrite saved state.");
            Require(right.CanCaptureViewState("right-tab", fixtures), "Loading left suppresses right pane state capture.");
            using (left.ViewStateSyncSuppression.Enter()) { }
            Require(!left.CanCaptureViewState("left-tab", fixtures), "Overlapping restore released suppression early.");
        }
        Require(left.CanCaptureViewState("left-tab", fixtures), "Completed restore still suppresses capture.");
        Require(!left.CanCaptureViewState("right-tab", fixtures), "Same-folder subtab identity was ignored.");
        Require(!left.CanCaptureViewState("left-tab", Path.Combine(fixtures, "other")), "Navigation path was ignored.");
        left.BeginLoad("next-tab", fixtures);
        left.BeginStagedItems(fixtures, "next-tab");
        Require(!left.CanCaptureViewState("next-tab", fixtures), "Incomplete publication was saved as empty state.");
        left.IsPublishingItems = true;
        typeof(FileListState).GetProperty(nameof(FileListState.HasPublicationInteraction))!.SetValue(left, true);
        using (left.ViewStateSyncSuppression.Enter())
        {
            Require(left.CanCaptureViewState("next-tab", fixtures), "Explicit input during publication cannot be saved before switching away.");
            Require(!left.CanCaptureViewState("left-tab", fixtures), "Partial view was attributed to the previous tab.");
        }
        left.PendingAnchorScrollState = new AnchorScrollState("old-tab-file", 0, null, null, 10);
        left.BeginLoad("third-tab", fixtures);
        Require(!left.HasPublicationInteraction, "New load inherited earlier input intent.");
        Require(left.PendingAnchorScrollState is null, "New tab inherited previous tab's pending anchor.");
    }));

    await CheckAsync("obsolete-group-anchor-restore-does-not-scroll", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "stale-anchor");
        Directory.CreateDirectory(folder);
        var entries = Enumerable.Range(0, 120).Select(index =>
        {
            var path = Path.Combine(folder, $"{index:D4}.txt");
            File.WriteAllText(path, "anchor");
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        var list = new System.Windows.Controls.ListView { ItemsSource = entries, Width = 400, Height = 240 };
        System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, true);
        var host = new System.Windows.Window { Content = list, Width = 420, Height = 280, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show();
            list.UpdateLayout();
            System.Windows.Controls.ScrollViewer? FindScroll(System.Windows.DependencyObject parent)
            {
                if (parent is System.Windows.Controls.ScrollViewer found) return found;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
                return null;
            }
            var scroll = FindScroll(list)!;
            scroll.ScrollToVerticalOffset(10);
            list.UpdateLayout();
            var before = scroll.VerticalOffset;
            Require(before > 0, "Scroll fixture did not move away from the top.");
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var stale = false;
            var restore = typeof(FileEntry).Assembly.GetType("FileKakari.AnchorScrollService")!.GetMethod("RestoreAnchorScrollAsync")!;
            var task = (Task<bool>)restore.Invoke(null, new object[] { list,
                new AnchorScrollState(entries[100].FullPath, 0, null, null, 100), GroupMode.Extension, dispatcher,
                0L, (Func<bool>)(() => stale) })!;
            dispatcher.BeginInvoke(new Action(() => stale = true), System.Windows.Threading.DispatcherPriority.Input);
            PumpDispatcherUntilComplete(task);
            list.UpdateLayout();
            Require(!task.Result, "Obsolete anchor restore reported success.");
            Require(Math.Abs(scroll.VerticalOffset - before) < 0.5, "Old tab's delayed anchor scrolled the current view.");
        }
        finally { host.Close(); }
    }));

    await CheckAsync("range-selection-deltas-and-visible-grouped-rows", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "range-selection");
        Directory.CreateDirectory(folder);
        var entries = Enumerable.Range(0, 300).Select(index =>
        {
            var path = Path.Combine(folder, $"{index:D4}.{(index % 2 == 0 ? "txt" : "log")}");
            File.WriteAllText(path, "selection");
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        var list = new System.Windows.Controls.ListView
        {
            ItemsSource = entries, Width = 400, Height = 240,
            SelectionMode = System.Windows.Controls.SelectionMode.Extended
        };
        var assembly = typeof(FileEntry).Assembly;
        var shift = assembly.GetType("FileKakari.FileListSelectionHelper")!.GetMethod("PerformShiftSelection")!;
        var range = assembly.GetType("FileKakari.FileListRangeSelectionHelper")!.GetMethod("SelectItemsInRange")!;
        void Shift(FileEntry? anchor, FileEntry target, bool additive = false) =>
            shift.Invoke(null, new object?[] { list, anchor, target, additive });
        void Expect(IEnumerable<FileEntry> expected) => Require(
            list.SelectedItems.Cast<FileEntry>().ToHashSet().SetEquals(expected), "Selection differs from requested range.");
        var added = 0;
        var removed = 0;
        list.SelectionChanged += (_, e) => { added += e.AddedItems.Count; removed += e.RemovedItems.Count; };
        Shift(entries[0], entries[199]);
        Expect(entries.Take(200));
        added = removed = 0;
        Shift(entries[0], entries[199]);
        Require(added == 0 && removed == 0, "Unchanged Shift range reselected rows.");
        Shift(entries[0], entries[209]);
        Require(added == 10 && removed == 0, "Growing range changed retained selections.");
        Shift(entries[29], entries[10]);
        Expect(entries.Skip(10).Take(20));
        Shift(entries[99], entries[90], true);
        Expect(entries.Skip(10).Take(20).Concat(entries.Skip(90).Take(10)));
        Shift(null, entries[299]);
        Require(list.SelectedItems.Contains(entries[299]), "Missing anchor fallback lost the target.");

        // View order, not source order, defines Shift selection after sorting/filtering.
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(entries);
        view.Filter = item => ((FileEntry)item).Name.EndsWith(".txt", StringComparison.Ordinal);
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(FileEntry.Name), System.ComponentModel.ListSortDirection.Descending));
        var ordered = view.Cast<FileEntry>().ToArray();
        Shift(ordered[2], ordered[12]);
        Expect(ordered.Skip(2).Take(11));
        view.Filter = null;
        view.SortDescriptions.Clear();

        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(list, true);
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, true);
        // A presentation source is needed for the same IsVisible checks as the app.
        var host = new System.Windows.Window
        {
            Content = list, Width = 420, Height = 280, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            host.Show();
            foreach (var grouped in new[] { false, true })
            {
                if (grouped) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileEntry.Extension)));
                list.Measure(new System.Windows.Size(400, 240));
                list.Arrange(new System.Windows.Rect(0, 0, 400, 240));
                list.UpdateLayout();
                list.SelectedItems.Clear();
                range.Invoke(null, new object[] { list, new System.Windows.Rect(0, 0, 400, 240), false, new HashSet<FileEntry>() });
                Require(list.SelectedItems.Count > 0 && list.SelectedItems.Count < entries.Length,
                    $"Rectangle selection failed to select visible rows (grouped={grouped}).");
                var visible = list.SelectedItems.Cast<FileEntry>().ToHashSet();
                added = removed = 0;
                range.Invoke(null, new object[] { list, new System.Windows.Rect(0, 0, 400, 240), false, new HashSet<FileEntry>() });
                Require(added == 0 && removed == 0, "Stationary rectangle reselected rows.");
                range.Invoke(null, new object[] { list, new System.Windows.Rect(-100, -100, 1, 1), true, visible });
                Expect(visible);
                range.Invoke(null, new object[] { list, new System.Windows.Rect(-100, -100, 1, 1), false, visible });
                Expect(Array.Empty<FileEntry>());
            }
        }
        finally { host.Close(); }
    }));

    await CheckAsync("group-switch-after-layout-keeps-virtualization-mode", () => RunOnStaAsync(() =>
    {
        var path = Path.Combine(fixtures, "virtualization.txt");
        File.WriteAllText(path, "layout");
        var entry = FileEntry.FromFileBasic(new FileInfo(path));
        var state = new FileListState();
        state.ReplaceItems(fixtures, Enumerable.Repeat(entry, 200).ToArray());
        var list = new System.Windows.Controls.ListView { ItemsSource = state.ItemsView, Width = 400, Height = 240 };
        System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(list, System.Windows.Controls.VirtualizationMode.Recycling);
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        var helper = typeof(FileListState).Assembly.GetType("FileKakari.FileGroupHelper")!;
        var apply = helper.GetMethod("ApplyGroupMode", new[] { typeof(System.Windows.Controls.ListView), typeof(GroupMode), typeof(SimilarNameGroupIndex) })!;
        void Layout()
        {
            list.Measure(new System.Windows.Size(400, 240));
            list.Arrange(new System.Windows.Rect(0, 0, 400, 240));
            list.UpdateLayout();
        }
        Layout();
        foreach (var mode in new[] { GroupMode.Extension, GroupMode.None, GroupMode.Date, GroupMode.SimilarName, GroupMode.None })
        {
            apply.Invoke(null, new object?[] { list, mode, mode == GroupMode.SimilarName ? state.EnsureSimilarNameIndex() : null });
            list.InvalidateMeasure();
            Layout();
            Require(System.Windows.Controls.VirtualizingPanel.GetVirtualizationMode(list) == System.Windows.Controls.VirtualizationMode.Recycling,
                "Changing groups after layout must not switch the virtualization mode.");
        }
    }));

    await CheckAsync("staged-pane-publication-selection-cancel-and-ownership", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "staged-pane");
        Directory.CreateDirectory(folder);
        var entries = Enumerable.Range(0, 300).Select(index =>
        {
            var path = Path.Combine(folder, $"{index:D4}.txt");
            File.WriteAllText(path, "stage");
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        var state = new FileListState();
        var view = new System.Windows.Controls.ListView { ItemsSource = state.ItemsView };
        var resets = 0;
        var batches = 0;
        var yielded = false;
        state.ItemsView.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        var load = StagedFileListPublisher.PublishAsync(state, entries, folder, "stage", () => true,
            () => state.ApplySort("Name", true, true),
            (count, _, _) =>
            {
                batches++;
                Require(!state.IsLoadedFor("stage", folder), "Partial rows must never be considered a completed load.");
                if (batches == 1)
                {
                    view.SelectedItem = entries[0];
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => yielded = true), System.Windows.Threading.DispatcherPriority.Input);
                }
            }, CancellationToken.None);
        PumpDispatcherUntilComplete(load);
        Require(load.Result && batches > 1 && yielded, "Publication must yield between batches.");
        Require(state.Items.SequenceEqual(entries) && ReferenceEquals(view.SelectedItem, entries[0]), "Appending rows must preserve order and existing selection.");
        Require(resets == 1 && !state.IsPublishingItems, "Publication must reset only when replacing the old list.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = StagedFileListPublisher.PublishAsync(state, entries, folder, "cancel", () => true,
            () => state.ApplySort("Name", true, true), (_, _, _) => cancellation.Cancel(), cancellation.Token);
        try { PumpDispatcherUntilComplete(cancelled); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Require(state.Items.Count == 64 && state.LastLoadedAt is null && !state.IsPublishingItems, "Cancellation must leave an incomplete, reloadable list.");

        var owner = true;
        var obsolete = StagedFileListPublisher.PublishAsync(state, entries, folder, "obsolete", () => owner,
            () => state.ApplySort("Name", true, true), (_, _, _) => owner = false, CancellationToken.None);
        PumpDispatcherUntilComplete(obsolete);
        Require(!obsolete.Result && state.Items.Count == 64 && state.LastLoadedAt is null, "An obsolete load must stop publishing.");
        state.BeginLoad("next", folder);
        Require(!state.IsPublishingItems, "A replacement load must clear obsolete publication state.");
    }));

    await CheckAsync("adaptive-ui-batch-budget-and-load-isolation", () =>
    {
        var sizer = new AdaptiveBatchSizer();
        Require(sizer.NextSize == 64, "First display must retain the small initial batch.");
        for (var i = 0; i < 20; i++) sizer.Observe(sizer.NextSize, 1);
        Require(sizer.NextSize == 2048, "Cheap UI updates must grow to the bounded throughput limit.");
        sizer.Observe(2048, 24);
        Require(sizer.NextSize < 2048, "An over-budget UI update must reduce the next batch.");
        var stableSize = sizer.NextSize;
        sizer.Observe(stableSize, 6);
        Require(sizer.NextSize == stableSize, "Normal timing variation must not oscillate the batch size.");
        for (var i = 0; i < 20; i++) sizer.Observe(sizer.NextSize, 40);
        Require(sizer.NextSize == 64, "Heavy UI updates must retain a nonzero lower bound.");
        sizer.Observe(0, 0);
        sizer.Observe(2048, double.NaN);
        sizer.Observe(2048, double.PositiveInfinity);
        Require(sizer.NextSize == 64, "Skipped or invalid measurements must not change the budget.");
        sizer.Observe(64, 0);
        Require(sizer.NextSize > 64 && new AdaptiveBatchSizer().NextSize == 64, "A new load must not inherit another load's feedback.");

        var rows = Enumerable.Range(0, 10003).ToArray();
        var published = new List<int>();
        for (var offset = 0; offset < rows.Length;)
        {
            var count = Math.Min(sizer.NextSize, rows.Length - offset);
            published.AddRange(rows.AsSpan(offset, count).ToArray());
            offset += count;
            sizer.Observe(count, offset % 3 == 0 ? 16 : 1);
        }
        Require(rows.SequenceEqual(published), "Changing batch boundaries must not drop, duplicate or reorder entries.");
        return Task.CompletedTask;
    });

    await CheckAsync("theme-upgrade-preserves-custom-edits", () =>
    {
        var source = Path.Combine(fixtures, "new-theme.xaml");
        var target = Path.Combine(fixtures, "user-theme.xaml");
        const string original = "original\ntheme\n";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(original)));
        File.WriteAllText(source, "new palette");
        File.WriteAllText(target, original.Replace("\n", "\r\n"));
        Require(BundledThemeUpgrade.TryApply(source, target, hash) && File.ReadAllText(target) == "new palette", "An untouched bundled theme must upgrade regardless of line endings.");
        File.WriteAllText(target, "my edited palette");
        Require(!BundledThemeUpgrade.TryApply(source, target, hash) && File.ReadAllText(target) == "my edited palette", "User edits must survive upgrades.");
        Require(!BundledThemeUpgrade.TryApply(source, Path.Combine(fixtures, "missing-theme.xaml"), hash), "Missing copies must use normal installation.");
        Require(ThemeManager.ResolveLegacyThemeName("CyberpunkTheme.xaml") == "CyberpunkDarkTheme.xaml"
            && ThemeManager.ResolveLegacyThemeName("ForestTheme.xaml") == "ForestLightTheme.xaml"
            && ThemeManager.ResolveLegacyThemeName("GrayTheme.xaml") == "GraphiteDarkTheme.xaml"
            && ThemeManager.ResolveLegacyThemeName("CappuccinoTheme.xaml") == "CappuccinoDarkTheme.xaml"
            && ThemeManager.ResolveLegacyThemeName("PersonalTheme.xaml") == "PersonalTheme.xaml", "Legacy selection migration changed an unrelated custom theme.");
        return Task.CompletedTask;
    });

    await CheckAsync("bundled-theme-pairs-resources-and-contrast", () => RunOnStaAsync(() =>
    {
        var themeDirectory = Path.Combine(AppContext.BaseDirectory, "Themes");
        var pairNames = new[] { "", "Graphite", "Cappuccino", "Forest", "Ocean", "Sakura", "Lavender", "Amber", "Rose", "Cyberpunk", "Matrix", "Synthwave", "DeepSpace", "Hologram", "Steampunk" };
        var expectedKeys = new HashSet<string>();
        foreach (var name in pairNames)
        foreach (var mode in new[] { "Light", "Dark" })
        {
            var fileName = $"{name}{mode}Theme.xaml";
            using var stream = File.OpenRead(Path.Combine(themeDirectory, fileName));
            var dictionary = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            var keys = dictionary.Keys.Cast<string>().ToHashSet();
            if (expectedKeys.Count == 0) expectedKeys = keys;
            Require(expectedKeys.SetEquals(keys), $"Theme resources differ: {fileName}");
            foreach (var key in new[] { "PanelBackground", "ControlBackground", "HeaderBackground", "RowHover", "TabBackground", "TabSelectedBackground" })
            {
                Require(ThemeContrast((Color)dictionary["TextColor"], (Color)dictionary[key + "Color"]) >= 4.5,
                    $"Insufficient text contrast: {fileName}/{key}");
            }
            foreach (var key in new[] { "PanelBackground", "ControlBackground", "HeaderBackground" })
                Require(ThemeContrast((Color)dictionary["SubtleTextColor"], (Color)dictionary[key + "Color"]) >= 4.5,
                    $"Insufficient secondary text contrast: {fileName}/{key}");
            foreach (var key in new[] { "SelectionBackground", "SelectionInactiveBackground" })
                Require(ThemeContrast((Color)dictionary["SelectionTextColor"], (Color)dictionary[key + "Color"]) >= 4.5,
                    $"Insufficient selection text contrast: {fileName}/{key}");
            Require(ThemeContrast((Color)dictionary["ActivePaneAccentColor"], (Color)dictionary["PanelBackgroundColor"]) >= 3,
                $"Insufficient active pane marker contrast: {fileName}");
            Require(dictionary["PanelBackgroundBrush"] is SolidColorBrush && dictionary["ActivePaneAccentBrush"] is SolidColorBrush,
                $"Required brushes missing: {fileName}");
        }
    }));

    await CheckAsync("pane-selection-colors-follow-active-pane", () => RunOnStaAsync(() =>
    {
        using var themeStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Themes", "ThemeResources.xaml"));
        var resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(themeStream);
        foreach (var key in new object[] { typeof(System.Windows.Controls.ListViewItem), "ContentListViewItemStyle" })
        {
            var item = new System.Windows.Controls.ListViewItem { Style = (System.Windows.Style)resources[key], IsSelected = true };
            item.Resources["SelectionBackgroundBrush"] = Brushes.Blue;
            item.Resources["SelectionInactiveBackgroundBrush"] = Brushes.Gray;
            var parent = new System.Windows.Controls.Border { Child = item };
            item.ApplyTemplate();
            var root = (System.Windows.Controls.Border)item.Template.FindName("Root", item);
            Require(ReferenceEquals(root.Background, Brushes.Gray), "An ordinary unfocused list must keep its inactive selection color.");
            PaneSelection.SetIsActive(parent, true);
            Require(ReferenceEquals(root.Background, Brushes.Blue), "The active pane must use its active selection color without focusing the row.");
            PaneSelection.SetIsActive(parent, false);
            Require(ReferenceEquals(root.Background, Brushes.Gray), "A deactivated pane must use its inactive selection color.");
            PaneSelection.SetIsActive(parent, true);
            Require(ReferenceEquals(root.Background, Brushes.Blue) && item.IsSelected && !item.IsKeyboardFocusWithin,
                "Reactivation must update the color without reselecting or moving focus.");
            PaneSelection.SetIsActive(parent, null);
            Require(ReferenceEquals(root.Background, Brushes.Gray), "Removing pane ownership must restore keyboard-focus coloring.");
        }
    }));

    await CheckAsync("workspace-live-view-reuse-boundaries", () => RunOnStaAsync(() =>
    {
        var path = Path.Combine(fixtures, "view-reuse.txt");
        File.WriteAllText(path, "reuse");
        var entry = FileEntry.FromFileBasic(new FileInfo(path));
        var tab = new FolderTab(fixtures);
        var pane = new FolderPane("reuse", new System.Collections.ObjectModel.ObservableCollection<FolderTab> { tab });
        pane.FileList.ReplaceItems(fixtures, new[] { entry }, loadedStateId: tab.Id);
        pane.FileList.ApplySort("Name", true, true);
        tab.State.VerticalOffset = 120;
        tab.State.SelectedPaths = new[] { path };
        var view = new System.Windows.Controls.ListView { DataContext = pane, ItemsSource = pane.ItemsView };
        view.SelectedItem = entry;
        var resets = 0;
        pane.ItemsView.CollectionChanged += (_, _) => resets++;
        Require(WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "An unchanged live view should be preserved.");
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 0, true), "A reset scroll position still needs restoration.");
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, null, true), "A missing scroll viewer still needs restoration.");
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, false), "Changed folder ordering still needs restoration.");
        view.SelectedItem = null;
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "Lost selection still needs restoration.");
        view.SelectedItem = entry;
        tab.State.SortAscending = false;
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "Changed sorting still needs restoration.");
        tab.State.SortAscending = true;
        tab.State.FilterText = "changed";
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "Changed filtering still needs restoration.");
        tab.State.FilterText = "";
        tab.State.GroupMode = GroupMode.Extension;
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "Changed grouping still needs restoration.");
        tab.State.GroupMode = GroupMode.None;
        tab.State.MarkPendingExternalChange();
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "External changes must not reuse the view.");
        tab.State.ClearPendingExternalChange();
        pane.IsLoading = true;
        Require(!WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true), "An unfinished load must not reuse the view.");
        pane.IsLoading = false;
        Require(WorkspacePaneViewReuse.CanPreserve(pane, view, 120, true) && resets == 0,
            "Reuse checks must leave the live collection view untouched.");
    }));

    await CheckAsync("pane-activation-loaded-state-boundaries", () => RunOnStaAsync(() =>
    {
        var tab = new FolderTab(fixtures);
        var otherTab = new FolderTab(fixtures);
        var pane = new FolderPane("activation", new System.Collections.ObjectModel.ObservableCollection<FolderTab> { tab, otherTab });
        Require(!pane.IsActiveStateLoaded, "An unloaded pane must load on activation.");
        pane.FileList.ReplaceItems(fixtures, [], loadedStateId: tab.Id);
        Require(pane.IsActiveStateLoaded, "An already displayed tab must preserve its live view.");
        tab.State.MarkPendingExternalChange();
        Require(!pane.IsActiveStateLoaded, "External changes must still trigger loading.");
        tab.State.ClearPendingExternalChange();
        pane.SelectedTabIndex = 1;
        Require(!pane.IsActiveStateLoaded, "A different tab at the same path must restore its own state.");
        pane.SelectedTabIndex = 0;
        Require(pane.IsActiveStateLoaded, "Returning to the displayed tab must preserve its view.");
        tab.State.CurrentPath = Path.Combine(fixtures, "different-path");
        Require(!pane.IsActiveStateLoaded, "Navigation to a different path must still load.");
    }));

    await CheckAsync("pane-single-sort-matches-previous-pipeline", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "pane-sorting");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "folder-z"));
        Directory.CreateDirectory(Path.Combine(folder, "folder-a"));
        foreach (var name in new[] { "b.txt", "a.txt", "c.bin", "日本語.txt", "plain" })
            File.WriteAllText(Path.Combine(folder, name), name.Length % 2 == 0 ? "one" : "longer");
        var enumerator = new FileSystemEnumerator();
        foreach (var column in new[] { "Name", "Kind", "Size", "ModifiedAt", "CreatedAt", "AccessedAt", "Extension", "Attributes", "FullPath", "ParentPath", "BaseName" })
        foreach (var ascending in new[] { true, false })
        foreach (var foldersFirst in new[] { true, false })
        {
            var previous = new FileListState();
            previous.ReplaceItems(folder, enumerator.Enumerate(folder, column, ascending, foldersFirst, true, CancellationToken.None).ToList());
            previous.ApplySort(column, ascending, foldersFirst);
            var current = new FileListState();
            using (current.ItemsView.DeferRefresh())
            {
                current.ReplaceItems(folder, enumerator.Enumerate(folder, "__none", ascending, foldersFirst, true, CancellationToken.None).ToList());
                current.ApplySort(column, ascending, foldersFirst);
            }
            Require(previous.ItemsView.Cast<FileEntry>().Select(entry => entry.FullPath).SequenceEqual(current.ItemsView.Cast<FileEntry>().Select(entry => entry.FullPath)),
                $"Sort order changed: {column}, ascending={ascending}, foldersFirst={foldersFirst}");
        }
    }));

    await CheckAsync("wpf-deferred-replace-sort-filter-single-refresh", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "deferred-sort");
        Directory.CreateDirectory(folder);
        var entries = new List<FileEntry>();
        foreach (var name in new[] { "b.txt", "c.bin", "a.txt" })
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, name);
            entries.Add(FileEntry.FromFileBasic(new FileInfo(path)));
        }
        var state = new FileListState();
        var resets = 0;
        state.ItemsView.CollectionChanged += (_, _) => resets++;
        using (state.ItemsView.DeferRefresh())
        {
            state.ReplaceItems(folder, entries);
            state.ApplySort("Name", true, true);
            CollectionViewUpdate.SetFilterAndRefresh(state.ItemsView, item => ((FileEntry)item).Extension == "txt", false);
        }
        Require(resets == 1 && state.ItemsView.Cast<FileEntry>().Select(entry => entry.Name).SequenceEqual(new[] { "a.txt", "b.txt" }), "Deferred update refreshed multiple times or lost sorting/filtering.");
        using (state.ItemsView.DeferRefresh())
        {
            state.ApplySort("Name", false, true);
            CollectionViewUpdate.SetFilterAndRefresh(state.ItemsView, null, false);
        }
        Require(resets == 2 && CollectionViewUpdate.Count(state.ItemsView) == 3, "Deferred filter removal did not restore all rows.");
        using (state.ItemsView.DeferRefresh())
        {
            state.ApplySort("Name", true, true);
            CollectionViewUpdate.SetFilterAndRefresh(state.ItemsView, null, false);
        }
        Require(resets == 3 && state.ItemsView.Cast<FileEntry>().First().Name == "a.txt", "Unchanged null filter refreshed during deferral.");
    }));

    await CheckAsync("group-representative-order-and-ties", () =>
    {
        var folder = Path.Combine(fixtures, "representatives");
        Directory.CreateDirectory(folder);
        var entries = new List<FileEntry>();
        foreach (var name in new[] { "Ａ.txt", "a.txt", "B.txt", "C.pdf" })
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, name.StartsWith("B") ? "longer" : "x");
            entries.Add(FileEntry.FromFile(new FileInfo(path)));
        }
        var ascending = GroupRepresentativeIndex.Build(entries, GroupMode.Extension, "Name", true, true);
        var descending = GroupRepresentativeIndex.Build(entries, GroupMode.Extension, "Name", false, true);
        Require(ReferenceEquals(ascending.GetRepresentative("txt"), entries[0]), "Normalized name tie must retain first representative.");
        Require(ReferenceEquals(descending.GetRepresentative("txt"), entries[2]), "Descending representative changed.");
        var size = GroupRepresentativeIndex.Build(entries, GroupMode.Extension, "Size", false, true);
        Require(ReferenceEquals(size.GetRepresentative("txt"), entries[2]), "Largest representative not selected.");
        Require(ReferenceEquals(size.GetRepresentative("pdf"), entries[3]), "Single-member group changed.");
        entries[0].Name = "Z.txt";
        var updated = GroupRepresentativeIndex.Build(entries, GroupMode.Extension, "Name", false, true);
        Require(ReferenceEquals(updated.GetRepresentative("txt"), entries[0]), "Rebuild reused stale name keys.");
        return Task.CompletedTask;
    });

    await CheckAsync("wpf-filter-single-refresh-and-count", () => RunOnStaAsync(() =>
    {
        var items = new BulkObservableCollection<int> { 1, 2, 3 };
        var view = new System.Windows.Data.ListCollectionView(items);
        var resets = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)view).CollectionChanged += (_, _) => resets++;
        var calls = 0;
        var minimum = 2;
        Predicate<object> filter = item => { calls++; return (int)item >= minimum; };
        CollectionViewUpdate.SetFilterAndRefresh(view, filter);
        Require(resets == 1 && calls == 3 && CollectionViewUpdate.Count(view) == 2, "Filter evaluated more than once or count is incorrect.");
        minimum = 3;
        CollectionViewUpdate.SetFilterAndRefresh(view, filter);
        Require(resets == 2 && calls == 6 && CollectionViewUpdate.Count(view) == 1, "Reused predicate did not refresh changed data.");
        CollectionViewUpdate.SetFilterAndRefresh(view, null);
        Require(resets == 3 && CollectionViewUpdate.Count(view) == 3, "Clearing filter did not restore all items in one refresh.");
        items.ReplaceAll(new[] { 4, 5 });
        Require(resets == 4 && CollectionViewUpdate.Count(view) == 2, "Collection replacement did not update the view once.");
    }));

    await CheckAsync("wpf-pane-replace-preserves-sort-filter", () => RunOnStaAsync(() =>
    {
        var path = Path.Combine(fixtures, "view-a.txt");
        File.WriteAllText(path, "a");
        var first = FileEntry.FromFileBasic(new FileInfo(path));
        var secondPath = Path.Combine(fixtures, "view-b.txt");
        File.WriteAllText(secondPath, "bb");
        var second = FileEntry.FromFileBasic(new FileInfo(secondPath));
        var state = new FileListState();
        state.ApplySort("Name", false, true);
        CollectionViewUpdate.SetFilterAndRefresh(state.ItemsView, item => ((FileEntry)item).Name.EndsWith(".txt"));
        var notifications = 0;
        state.ItemsView.CollectionChanged += (_, _) => notifications++;
        state.ReplaceItems(fixtures, new[] { first, second });
        Require(notifications == 1 && state.ItemsView.Cast<FileEntry>().SequenceEqual(new[] { second, first }), "Pane replacement lost sort/filter or refreshed multiple times.");
        state.ReplaceItems(fixtures, Array.Empty<FileEntry>());
        Require(notifications == 2 && CollectionViewUpdate.Count(state.ItemsView) == 0, "Empty pane retained stale rows.");
    }));

    await CheckAsync("collection-replace-single-reset", () =>
    {
        var items = new BulkObservableCollection<int> { 1, 2, 3 };
        var notifications = 0;
        items.CollectionChanged += (_, args) =>
        {
            Require(args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset, "Replacement must be one reset.");
            notifications++;
        };
        items.ReplaceAll(items.Where(item => item != 2));
        Require(items.SequenceEqual(new[] { 1, 3 }) && notifications == 1, "Aliased replacement lost data or emitted multiple resets.");
        IEnumerable<int> FailingSource()
        {
            yield return 4;
            throw new IOException("Enumeration failed.");
        }
        try { items.ReplaceAll(FailingSource()); }
        catch (IOException) { }
        Require(items.SequenceEqual(new[] { 1, 3 }) && notifications == 1, "Failed replacement changed the collection.");
        items.ReplaceAll(Array.Empty<int>());
        Require(items.Count == 0 && notifications == 2, "Empty replacement was not notified.");
        return Task.CompletedTask;
    });

    foreach (var sample in new[]
    {
        ("日本語 空白 & #.txt", "日本語のプレビュー\r\nsecond line", (Encoding)new UTF8Encoding(false), "utf-8"),
        ("cp932.bat", "@echo off\r\necho 接続確認", Encoding.GetEncoding(932), "cp932"),
        ("ascii.cmd", "@echo off\r\necho TEST", Encoding.ASCII, "utf-8"),
        ("readme.md", "# Markdown\n本文", new UTF8Encoding(false), "utf-8")
    })
    {
        await CheckAsync($"text-{sample.Item1}", async () =>
        {
            var path = Path.Combine(fixtures, sample.Item1);
            await File.WriteAllTextAsync(path, sample.Item2, sample.Item3);
            var preview = await textProvider.CreatePreviewAsync(new PreviewRequest(path), CancellationToken.None);
            Require(preview.Status == FilePreviewStatus.Success && preview.Kind == FilePreviewKind.Text,
                $"Expected text success, got {preview.Status}/{preview.Kind}: {preview.ErrorMessage}");
            Require(preview.Text == sample.Item2 && preview.EncodingName == sample.Item4,
                $"Text or encoding mismatch: {preview.EncodingName}");
        });
    }

    await CheckAsync("text-too-large", async () =>
    {
        var path = Path.Combine(fixtures, "large.txt");
        using (var stream = File.Create(path)) stream.SetLength(BuiltInTextPreviewProvider.MaxTextBytes + 1);
        var preview = await textProvider.CreatePreviewAsync(new PreviewRequest(path), CancellationToken.None);
        Require(preview.Status == FilePreviewStatus.TooLarge, $"Expected TooLarge, got {preview.Status}");
    });
    await CheckAsync("text-missing", async () =>
    {
        var preview = await textProvider.CreatePreviewAsync(new PreviewRequest(Path.Combine(fixtures, "missing.txt")), CancellationToken.None);
        Require(preview.Status == FilePreviewStatus.Missing, $"Expected Missing, got {preview.Status}");
    });
    await CheckAsync("text-cancelled", async () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await textProvider.CreatePreviewAsync(new PreviewRequest(Path.Combine(fixtures, "ascii.cmd")), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancelled text read completed without cancellation.");
    });
    await CheckAsync("preview-pane-preserves-file-list", () =>
    {
        Require(PreviewPaneSizing.Clamp(700, 500, 100, 180, 5) == 315, "Large preview hides file list after shrink.");
        Require(PreviewPaneSizing.Clamp(315, 250, 100, 180, 5) == 65, "Preview minimum must yield to file list.");
        Require(PreviewPaneSizing.Clamp(315, 180, 100, 180, 5) == 0, "Preview must collapse when no space remains.");
        Require(PreviewPaneSizing.Clamp(210, 900, 100, 180, 5) == 210, "Normal requested pane size changed.");
        return Task.CompletedTask;
    });
    await CheckAsync("image-cancelled", async () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await new BuiltInImagePreviewProvider().CreatePreviewAsync(new PreviewRequest(Path.Combine(fixtures, "missing.png")), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancelled image request completed without cancellation.");
    });
    await CheckAsync("image-fit-bounds", () =>
    {
        Require(ImagePreviewSizing.Fit(1000, 10000, 1000, 200) == (20, 200), "Portrait must fit height.");
        Require(ImagePreviewSizing.Fit(10000, 1000, 200, 1000) == (200, 20), "Panorama must fit width.");
        Require(ImagePreviewSizing.Fit(20, 10, 2000, 1000) == (20, 10), "Do not upscale original.");
        var bounded = ImagePreviewSizing.Fit(30000, 30000, 30000, 30000);
        Require((long)bounded.Width * bounded.Height <= ImagePreviewSizing.MaxDecodedPixels, "Decoded pixel budget exceeded.");
        return Task.CompletedTask;
    });
    await CheckAsync("image-portrait-cache-and-invalidation", async () =>
    {
        var path = Path.Combine(fixtures, "portrait.png");
        var bitmap = BitmapSource.Create(120, 1200, 96, 96, PixelFormats.Bgra32, null, new byte[120 * 1200 * 4], 120 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var provider = new BuiltInImagePreviewProvider();
        var request = new PreviewRequest(path) { TargetWidthDip = 600, TargetHeightDip = 120 };
        var first = await provider.CreatePreviewAsync(request, CancellationToken.None);
        Require(first.ImageSource is { PixelWidth: 12, PixelHeight: 120 }, "Portrait decoded beyond viewport bounds.");
        var second = await provider.CreatePreviewAsync(request, CancellationToken.None);
        Require(ReferenceEquals(first.ImageSource, second.ImageSource), "Unchanged image was not reused.");
        var highDpi = await provider.CreatePreviewAsync(request with { DpiScaleX = 2, DpiScaleY = 2 }, CancellationToken.None);
        Require(highDpi.ImageSource is { PixelWidth: 24, PixelHeight: 240 }, "DPI scaling did not update decode size.");
        var larger = await provider.CreatePreviewAsync(request with { TargetHeightDip = 480 }, CancellationToken.None);
        Require(larger.ImageSource is { PixelWidth: 48, PixelHeight: 480 }, "Larger viewport did not upgrade resolution.");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var updated = await provider.CreatePreviewAsync(request, CancellationToken.None);
        Require(!ReferenceEquals(larger.ImageSource, updated.ImageSource), "Modified image reused stale bitmap.");
        File.Delete(path);
        var missing = await provider.CreatePreviewAsync(request, CancellationToken.None);
        Require(missing.Status == FilePreviewStatus.Missing, "Deleted image was served from cache.");
    });
    await CheckAsync("image-corrupt", async () =>
    {
        var path = Path.Combine(fixtures, "corrupt.png");
        await File.WriteAllTextAsync(path, "not an image");
        var result = await new BuiltInImagePreviewProvider().CreatePreviewAsync(new PreviewRequest(path), CancellationToken.None);
        Require(result.Status == FilePreviewStatus.Failed, "Corrupt image did not produce a failure result.");
    });
    await CheckAsync("image-decode", async () =>
    {
        var path = Path.Combine(fixtures, "image.png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var preview = await new BuiltInImagePreviewProvider().CreatePreviewAsync(
            new PreviewRequest(path) { TargetWidthDip = 2, TargetHeightDip = 2 }, CancellationToken.None);
        Require(preview.Status == FilePreviewStatus.Success && preview.ImageSource is { IsFrozen: true, PixelWidth: > 0 },
            $"Image decoding failed: {preview.Status}: {preview.ErrorMessage}");
    });
    await CheckAsync("enumeration-sort-and-metadata", () =>
    {
        var folder = Path.Combine(fixtures, "listing");
        Directory.CreateDirectory(Path.Combine(folder, "z-folder"));
        File.WriteAllText(Path.Combine(folder, "b.txt"), "bb", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a", new UTF8Encoding(false));
        var enumerator = new FileSystemEnumerator();
        var entries = enumerator.Enumerate(folder, "Name", true, true, true, CancellationToken.None).ToList();
        Require(entries.Select(e => e.Name).SequenceEqual(new[] { "z-folder", "a.txt", "b.txt" }), "Folders-first/name order changed.");
        Require(entries[0].IsDirectory && entries[1].Size == 1 && entries[2].Size == 2, "Entry metadata mismatch.");
        var descending = enumerator.Enumerate(folder, "Name", false, true, true, CancellationToken.None).ToList();
        Require(descending.Select(e => e.Name).SequenceEqual(new[] { "z-folder", "b.txt", "a.txt" }), "Descending order changed.");
        return Task.CompletedTask;
    });
    await CheckAsync("session-roundtrip", () =>
    {
        var path = Path.Combine(fixtures, "session.json");
        var service = new SessionStateService();
        var state = new SessionState
        {
            Tabs = [new SessionTabState { TabId = "test-tab", Path = fixtures, FilterText = "日本語", ScrollOffset = 42, SelectedPaths = ["a.txt"] }]
        };
        Require(service.Save(path, state), "Session save failed.");
        var loaded = service.Load(path);
        Require(loaded.IsSuccess && loaded.State!.Tabs.Count == 1, "Session load failed.");
        var tab = loaded.State!.Tabs[0];
        Require(tab.TabId == "test-tab" && tab.Path == fixtures && tab.FilterText == "日本語" &&
            tab.ScrollOffset == 42 && tab.SelectedPaths.SequenceEqual(new[] { "a.txt" }), "Session state changed during roundtrip.");
        return Task.CompletedTask;
    });
    await CheckAsync("undo-retry-after-partial-move", async () =>
    {
        var folder = Path.Combine(fixtures, "undo");
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.txt");
        var b = Path.Combine(folder, "b.txt");
        var movedA = Path.Combine(folder, "moved-a.txt");
        var movedB = Path.Combine(folder, "moved-b.txt");
        File.WriteAllText(movedA, "a");
        File.WriteAllText(movedB, "b");
        File.WriteAllText(b, "collision");
        var undo = new UndoService(new FileOperationService());
        undo.RecordFileOperation(UndoOperationKind.Move,
            [new(a, movedA, false), new(b, movedB, false)]);
        try { await undo.UndoLastAsync(); throw new InvalidOperationException("Expected a collision."); }
        catch (IOException) { }
        Require(File.ReadAllText(a) == "a" && undo.LastAction is FileOperationUndoAction { Items.Count: 1 }, "Completed undo item retained.");
        File.Move(b, Path.Combine(folder, "collision.txt"));
        await undo.UndoLastAsync();
        Require(File.ReadAllText(b) == "b" && undo.LastAction is null, "Retry failed.");
    });
    await CheckAsync("watch-root-normalization", () =>
    {
        var root = Path.GetPathRoot(fixtures)!;
        Require(FolderWatchService.NormalizeFolderPath(root) == root, "Drive root became drive-relative.");
        Require(Path.IsPathFullyQualified(FolderWatchService.NormalizeFolderPath(root)), "Root is not fully qualified.");
        Require(FolderWatchService.NormalizeFolderPath(fixtures + Path.DirectorySeparatorChar) == fixtures, "Folder separator not normalized.");
        Require(FolderWatchService.NormalizeFolderPath(@"\\server\share\") == @"\\server\share", "UNC root changed.");
        return Task.CompletedTask;
    });
    await CheckAsync("copy-rejects-junction-cycle", async () =>
    {
        var source = Path.Combine(fixtures, "junction-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "keep.txt"), "unchanged");
        var link = Path.Combine(source, "parent-link");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; $null=New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET" }) start.ArgumentList.Add(arg);
        start.Environment["TEST_LINK"] = link;
        start.Environment["TEST_TARGET"] = source;
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, "Could not create test junction: " + await error);
        try
        {
            var result = await FileSystemOperations.CopySelfMultipleAsync([source], fixtures);
            Require(result.Error is IOException && result.ExecutedPaths.Count == 0, "Junction cycle was not rejected.");
            Require(File.ReadAllText(Path.Combine(source, "keep.txt")) == "unchanged", "Source changed.");
            Require(Directory.GetDirectories(fixtures, "junction-source*").Length == 1, "Partial copy created before rejecting junction.");
        }
        finally { Directory.Delete(link); } // Remove only the link, never recurse into its target.
    });
    await CheckAsync("enumeration-cancelled-before-io", () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        foreach (var column in new[] { "Name", "Kind", "ModifiedAt", "__none" })
        {
            try
            {
                new FileSystemEnumerator().Enumerate(Path.Combine(fixtures, "nonexistent"), column, true, true, true, cts.Token).ToList();
                throw new InvalidOperationException("Cancelled enumeration completed.");
            }
            catch (OperationCanceledException) { }
        }
        return Task.CompletedTask;
    });
    await CheckAsync("deferred-icon-notification", async () =>
    {
        var entry = FileEntry.FromFile(new FileInfo(Path.Combine(fixtures, "ascii.cmd")));
        Require(entry.Icon is null, "Entry creation eagerly loaded an icon.");
        var notified = false;
        entry.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(FileEntry.Icon);
        await entry.LoadIconAsync();
        Require(entry.Icon is not null && notified, "Deferred icon did not notify the binding.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await entry.LoadIconAsync(cancelled.Token); throw new InvalidOperationException("Cancelled icon request completed."); }
        catch (OperationCanceledException) { }
    });
    await CheckAsync("overlapping-viewstate-suppression", () =>
    {
        var suppression = new OperationSuppression();
        var first = suppression.Enter();
        var second = suppression.Enter();
        first.Dispose();
        Require(suppression.IsActive, "First completion released another operation's suppression.");
        first.Dispose();
        Require(suppression.IsActive, "Double disposal released another operation.");
        second.Dispose();
        Require(!suppression.IsActive, "Suppression leaked after completion.");
        try { using var failed = suppression.Enter(); throw new OperationCanceledException(); }
        catch (OperationCanceledException) { }
        Require(!suppression.IsActive, "Suppression leaked on cancellation.");
        return Task.CompletedTask;
    });
    await CheckAsync("atomic-save-preserves-previous-on-failure", () =>
    {
        var path = Path.Combine(fixtures, "atomic.json");
        AtomicFileWriter.WriteAllText(path, "{\"version\":1}");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { AtomicFileWriter.WriteAllText(path, "{\"version\":2}"); throw new InvalidOperationException("Expected locked destination to fail."); }
            catch (IOException) { }
        }
        Require(File.ReadAllText(path) == "{\"version\":1}", "Failed save damaged previous file.");
        Require(!Directory.EnumerateFiles(fixtures, ".atomic.json.*.tmp").Any(), "Temporary save file leaked.");
        AtomicFileWriter.WriteAllText(path, "{\"version\":3}");
        Require(File.ReadAllText(path) == "{\"version\":3}", "Atomic replacement failed.");
        return Task.CompletedTask;
    });
    await CheckAsync("session-save-request-order", async () =>
    {
        var path = Path.Combine(fixtures, "ordered-session.json");
        var service = new SessionStateService();
        var saves = Enumerable.Range(0, 20).Select(index => service.SaveAsync(path,
            new SessionState { SelectedTabIndex = index })).ToArray();
        Require((await Task.WhenAll(saves)).All(saved => saved), "Queued save failed.");
        Require(service.Load(path).State?.SelectedTabIndex == 19, "An older snapshot overwrote the latest save.");
        var queue = new SerializedTaskQueue();
        var failed = queue.Enqueue<int>(() => throw new IOException("test failure"));
        var next = queue.Enqueue(() => 42);
        try { await failed; } catch (IOException) { }
        Require(await next == 42, "A failed operation blocked the save queue.");
    });
    await CheckAsync("case-only-rename-and-undo", async () =>
    {
        var folder = Path.Combine(fixtures, "case-rename");
        Directory.CreateDirectory(folder);
        var service = new FileOperationService();
        foreach (var isDirectory in new[] { false, true })
        {
            var name = isDirectory ? "folder" : "report.txt";
            var renamed = isDirectory ? "Folder" : "Report.txt";
            var source = Path.Combine(folder, name);
            var target = Path.Combine(folder, renamed);
            if (isDirectory) Directory.CreateDirectory(source);
            else File.WriteAllText(source, "preserved");
            await service.RenameAsync(source, target, isDirectory);
            Require(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Contains(renamed, StringComparer.Ordinal), "Case-only rename was ignored.");
            var undo = new UndoService(service);
            undo.RecordRename(source, target, isDirectory);
            await undo.UndoLastAsync();
            Require(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Contains(name, StringComparer.Ordinal), "Case-only undo failed.");
            if (!isDirectory) Require(File.ReadAllText(source) == "preserved", "Rename changed contents.");
        }
        var occupied = Path.Combine(folder, "occupied.txt");
        File.WriteAllText(occupied, "existing");
        try { await service.RenameAsync(Path.Combine(folder, "report.txt"), occupied, false); throw new InvalidOperationException("Rename overwrote destination."); }
        catch (IOException) { }
        Require(File.ReadAllText(occupied) == "existing", "Existing target was changed.");
    });
    await CheckAsync("session-invalid-json", () =>
    {
        var path = Path.Combine(fixtures, "broken-session.json");
        File.WriteAllText(path, "{ invalid");
        Require(new SessionStateService().Load(path).Status == SessionStateLoadStatus.InvalidJson, "Invalid JSON was not reported.");
        return Task.CompletedTask;
    });
}
catch (Exception ex)
{
    checks.Add(new CheckResult("setup", false, ex.ToString()));
    Console.Error.WriteLine(ex);
}

var passed = checks.Count > 0 && checks.All(check => check.Passed);
await File.WriteAllTextAsync(Path.Combine(runDirectory, "results.json"), JsonSerializer.Serialize(
    new { SchemaVersion = 1, RunId = args[1], Passed = passed, Tests = checks }, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

async Task CheckAsync(string name, Func<Task> check)
{
    try
    {
        await check();
        checks.Add(new CheckResult(name, true, null));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        checks.Add(new CheckResult(name, false, ex.ToString()));
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

static void PumpDispatcherUntilComplete(Task task)
{
    if (!task.IsCompleted)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    task.GetAwaiter().GetResult();
}

static double ThemeContrast(Color first, Color second)
{
    static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
    static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    var a = Luminance(first);
    var b = Luminance(second);
    return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static Task RunOnStaAsync(Action action)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
                System.Windows.Threading.Dispatcher.CurrentDispatcher));
            action();
            completion.SetResult();
        }
        catch (Exception ex) { completion.SetException(ex); }
        finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
    }) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

internal sealed record CheckResult(string Name, bool Passed, string? Error);
