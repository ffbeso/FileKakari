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

    await CheckAsync("prepared-row-columns-do-not-depend-on-visual-parent", () => RunOnStaAsync(() =>
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Themes", "ThemeResources.xaml"));
        var resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
        foreach (var styleKey in new object[] { typeof(System.Windows.Controls.ListViewItem), "DiagnosticListViewItemStyle" })
        {
            var row = new System.Windows.Controls.ListViewItem { Content = "file", Style = (System.Windows.Style)resources[styleKey] };
            var columns = new System.Windows.Controls.GridViewColumnCollection();
            columns.Add(new System.Windows.Controls.GridViewColumn { Header = "Name", Width = 200 });
            // GridView prepares this attached property independently of the visual ancestry.
            System.Windows.Controls.GridView.SetColumnCollection(row, columns);
            row.ApplyTemplate(); row.Measure(new System.Windows.Size(400, 24));
            row.Arrange(new System.Windows.Rect(0, 0, 400, 24));
            var border = (System.Windows.Controls.Border)VisualTreeHelper.GetChild(row, 0);
            var presenter = (System.Windows.Controls.GridViewRowPresenter)border.Child;
            Require(ReferenceEquals(presenter.Columns, columns), "Prepared row lost columns while outside the ListView visual tree.");
            var replacement = new System.Windows.Controls.GridViewColumnCollection();
            replacement.Add(new System.Windows.Controls.GridViewColumn { Header = "Size", Width = 100 });
            System.Windows.Controls.GridView.SetColumnCollection(row, replacement);
            Require(ReferenceEquals(presenter.Columns, replacement), "Reprepared row retained the previous view's columns.");
        }
    }));

    await CheckAsync("grouped-row-content-survives-refresh-and-recycling", () => RunOnStaAsync(() =>
    {
        using var themeStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Themes", "ThemeResources.xaml"));
        var resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(themeStream);
        var state = new FileListState();
        var list = new System.Windows.Controls.ListView { Width = 500, Height = 280, ItemsSource = state.ItemsView,
            ItemContainerStyle = (System.Windows.Style)resources[typeof(System.Windows.Controls.ListViewItem)] };
        var columns = new System.Windows.Controls.GridView();
        columns.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "Name", Width = 300,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(FileEntry.Name)) });
        columns.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "Size", Width = 100,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(FileEntry.Size)) });
        list.View = columns;
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(list, System.Windows.Controls.VirtualizationMode.Recycling);
        System.Windows.Controls.VirtualizingPanel.SetScrollUnit(list, System.Windows.Controls.ScrollUnit.Pixel);
        state.ItemsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileEntry.Extension)));
        var host = new System.Windows.Window { Content = list, Width = 520, Height = 320, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        IEnumerable<System.Windows.DependencyObject> Children(System.Windows.DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var nested in Children(child)) yield return nested;
            }
        }
        try
        {
            host.Show();
            for (var round = 0; round < 12; round++)
            {
                var entries = Enumerable.Range(0, 1000).Select(i => new FileEntry
                {
                    Name = $"round{round}-file{i:D4}.type{i % 10}", FullPath = Path.Combine(fixtures, $"row{i}"), Kind = "file",
                    IsDirectory = false, Size = i, ModifiedAt = DateTime.MinValue, CreatedAt = DateTime.MinValue, AccessedAt = DateTime.MinValue
                }).ToArray();
                using (state.ItemsView.DeferRefresh())
                {
                    state.ReplaceItems(fixtures, entries);
                    state.ApplySort("Name", round % 2 == 0, true, groupMode: GroupMode.Extension);
                }
                list.ScrollIntoView(entries[(round * 83) % entries.Length]);
                list.UpdateLayout();
                PumpDispatcherUntilComplete(list.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle).Task);
                list.UpdateLayout();
                var rows = Children(list).OfType<System.Windows.Controls.ListViewItem>().Where(row => row.IsVisible && row.Content is FileEntry).ToArray();
                Require(rows.Length > 0, "Grouped fixture has no realized rows.");
                foreach (var row in rows)
                {
                    var presenter = Children(row).OfType<System.Windows.Controls.GridViewRowPresenter>().Single();
                    Require(ReferenceEquals(presenter.Columns, columns.Columns), $"Recycled row lost columns: round={round}");
                    Require(Children(presenter).OfType<System.Windows.Controls.TextBlock>().Any(text => text.Text == ((FileEntry)row.Content).Name),
                        $"Recycled row lost its file text: round={round}, file={((FileEntry)row.Content).Name}");
                }
            }
        }
        finally { host.Close(); }
    }));

    await CheckAsync("rectangle-selection-edge-scroll-and-reversal", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "rectangle-scroll");
        Directory.CreateDirectory(folder);
        var entries = Enumerable.Range(0, 300).Select(index =>
        {
            var path = Path.Combine(folder, $"{index:D4}.txt");
            File.WriteAllText(path, "row");
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        foreach (var grouped in new[] { false, true })
        foreach (var details in new[] { false, true })
        {
            var state = new FileListState();
            state.ReplaceItems(folder, entries);
            if (grouped) state.ItemsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileEntry.Extension)));
            var list = new System.Windows.Controls.ListView { ItemsSource = state.ItemsView, Width = 400, Height = 240,
                SelectionMode = System.Windows.Controls.SelectionMode.Extended };
            var gridView = new System.Windows.Controls.GridView();
            gridView.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "Name", Width = 350,
                DisplayMemberBinding = new System.Windows.Data.Binding(nameof(FileEntry.Name)) });
            if (details) list.View = gridView;
            System.Windows.Controls.VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
            System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(list, System.Windows.Controls.VirtualizationMode.Recycling);
            System.Windows.Controls.VirtualizingPanel.SetScrollUnit(list, System.Windows.Controls.ScrollUnit.Pixel);
            System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, true);
            var host = new System.Windows.Window { Content = list, Width = 420, Height = 280, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                host.Show(); list.UpdateLayout();
                System.Windows.Controls.ScrollViewer? FindScroll(System.Windows.DependencyObject parent)
                {
                    if (parent is System.Windows.Controls.ScrollViewer found) return found;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                        if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
                    return null;
                }
                var scroll = FindScroll(list)!;
                list.SelectedItems.Add(entries[^1]);
                var type = typeof(FileEntry).Assembly.GetType("FileKakari.ListViewRangeSelectionSession")!;
                var drawn = System.Windows.Rect.Empty;
                Action<System.Windows.Rect> draw = rect => drawn = rect;
                using var session = (IDisposable)Activator.CreateInstance(type, list, new System.Windows.Point(390, 60), true, draw)!;
                var apply = type.GetMethod("ApplySelection")!;
                var advance = type.GetMethod("AdvanceAtEdge", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                apply.Invoke(session, new object[] { new System.Windows.Point(100, 180) });
                Require(!drawn.IsEmpty && Math.Abs(drawn.Left - 100) < 0.1 && Math.Abs(drawn.Bottom - 180) < 0.1,
                    $"Rectangle does not reach the pointer below the column header (grouped={grouped}, drawn={drawn}).");
                var bottom = new System.Windows.Point(0, 260);
                type.GetMethod("CheckMove")!.Invoke(session, new object[] { bottom });
                apply.Invoke(session, new object[] { bottom });
                var initial = list.SelectedItems.Cast<FileEntry>().ToHashSet();
                Require(initial.Count > 1, "Rectangle fixture did not select visible rows.");
                for (var i = 0; i < 80; i++) advance.Invoke(session, new object[] { bottom });
                var expanded = list.SelectedItems.Count;
                Require(scroll.VerticalOffset > 500 && expanded > initial.Count, $"Bottom edge did not scroll and extend selection (grouped={grouped}).");
                Require(initial.All(item => list.SelectedItems.Contains(item)), "Recycling discarded selected rows above the viewport.");
                for (var i = 0; i < 100; i++) advance.Invoke(session, new object[] { new System.Windows.Point(0, -20) });
                Require(scroll.VerticalOffset == 0 && list.SelectedItems.Count < expanded, "Reversing at the top did not shrink selection.");
                Require(list.SelectedItems.Contains(entries[^1]), "Ctrl rectangle lost its original offscreen selection.");
                session.Dispose();
                advance.Invoke(session, new object[] { bottom });
                Require(scroll.VerticalOffset == 0, "Finished rectangle still auto-scrolled.");
            }
            finally { host.Close(); }
        }
    }));

    await CheckAsync("group-anchor-early-scroll-and-position-reuse", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "early-anchor");
        Directory.CreateDirectory(folder);
        var entries = Enumerable.Range(0, 300).Select(index =>
        {
            var path = Path.Combine(folder, $"{index:D4}.txt");
            File.WriteAllText(path, "row");
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        var state = new FileListState();
        state.ReplaceItems(folder, entries);
        state.ItemsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileEntry.Extension)));
        var list = new System.Windows.Controls.ListView { ItemsSource = state.ItemsView, Width = 400, Height = 240 };
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(list, System.Windows.Controls.VirtualizationMode.Recycling);
        System.Windows.Controls.VirtualizingPanel.SetScrollUnit(list, System.Windows.Controls.ScrollUnit.Pixel);
        System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, true);
        var host = new System.Windows.Window { Content = list, Width = 420, Height = 280, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); list.UpdateLayout();
            System.Windows.Controls.ScrollViewer? FindScroll(System.Windows.DependencyObject parent)
            {
                if (parent is System.Windows.Controls.ScrollViewer found) return found;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
                return null;
            }
            var scroll = FindScroll(list)!;
            var service = typeof(FileEntry).Assembly.GetType("FileKakari.AnchorScrollService")!;
            var capture = service.GetMethod("CaptureAnchorState")!;
            var restore = service.GetMethod("RestoreAnchorScrollAsync")!;
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            scroll.ScrollToVerticalOffset(1400); list.UpdateLayout();
            var anchor = (AnchorScrollState?)capture.Invoke(null, new object[] { list, GroupMode.Extension, scroll.VerticalOffset });
            Require(anchor is not null && scroll.VerticalOffset > 0, "Grouped fixture did not capture a scrolled anchor.");
            Task<bool> Restore(bool early, Func<bool>? stale = null, long flowId = 0) => (Task<bool>)restore.Invoke(null,
                new object?[] { list, anchor, GroupMode.Extension, dispatcher, flowId, stale, early })!;
            var unchanged = Restore(true);
            Require(unchanged.IsCompletedSuccessfully && unchanged.Result, "Valid visible anchor was not reused synchronously.");
            scroll.ScrollToVerticalOffset(0); list.UpdateLayout();
            var moved = Restore(true);
            PumpDispatcherUntilComplete(moved);
            Require(moved.Result, "Early grouped restoration did not converge.");
            var restored = (AnchorScrollState?)capture.Invoke(null, new object[] { list, GroupMode.Extension, scroll.VerticalOffset });
            Require(restored?.AnchorPath == anchor!.AnchorPath && Math.Abs(restored.RelativeOffset - anchor.RelativeOffset) <= 0.75,
                "Early restoration changed anchor or relative position.");
            var deletion = Restore(true, flowId: 1);
            Require(!deletion.IsCompleted, "Deletion bypassed its extent stabilization wait.");
            PumpDispatcherUntilComplete(deletion);
            Require(deletion.Result, "Conservative deletion restoration failed.");
            scroll.ScrollToVerticalOffset(0); list.UpdateLayout();
            var obsolete = false;
            var cancelled = Restore(true, () => obsolete);
            dispatcher.BeginInvoke(new Action(() => obsolete = true), System.Windows.Threading.DispatcherPriority.Normal);
            PumpDispatcherUntilComplete(cancelled);
            Require(!cancelled.Result && scroll.VerticalOffset == 0, "Obsolete early restore moved the new view.");
        }
        finally { host.Close(); }
    }));

    await CheckAsync("anchor-target-single-pass-and-fallback-priority", () =>
    {
        var entries = new[] { "next", "previous", "anchor" }.Select(name =>
        {
            var path = Path.Combine(fixtures, name);
            File.WriteAllText(path, name);
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        var anchor = new AnchorScrollState(entries[2].FullPath.ToUpperInvariant(), 0, entries[0].FullPath, entries[1].FullPath, 0);
        var enumerations = 0;
        IEnumerable<FileEntry> Once()
        {
            Require(++enumerations == 1, "Anchor lookup enumerated the view more than once.");
            foreach (var entry in entries) yield return entry;
            throw new InvalidOperationException("Exact anchor lookup scanned past its match.");
        }
        var exact = AnchorTargetResolver.Find(Once(), anchor);
        Require(ReferenceEquals(exact.Entry, entries[2]) && exact.Visited == 3, "Exact anchor did not outrank earlier fallback candidates.");
        Require(ReferenceEquals(AnchorTargetResolver.Find(entries.Take(2), anchor).Entry, entries[1]), "Previous fallback must outrank next even when encountered later.");
        Require(ReferenceEquals(AnchorTargetResolver.Find(entries.Take(1), anchor).Entry, entries[0]), "Next fallback was lost.");
        Require(AnchorTargetResolver.Find(Array.Empty<FileEntry>(), anchor).Entry is null, "Empty filtered view should use offset fallback.");
        return Task.CompletedTask;
    });

    await CheckAsync("async-column-sort-latest-selection-filter-and-owner", () => RunOnStaAsync(() =>
    {
        var state = new FileListState();
        var entries = Enumerable.Range(0, 4096).Select(i => new FileEntry
        {
            Name = $"row{i:D5}.txt", FullPath = Path.Combine(fixtures, $"sort{i:D5}.txt"), Kind = "text",
            IsDirectory = false, Size = i, ModifiedAt = DateTime.MinValue,
            CreatedAt = DateTime.MinValue, AccessedAt = DateTime.MinValue
        }).ToArray();
        state.ReplaceItems(fixtures, entries);
        var view = (System.Windows.Data.ListCollectionView)state.ItemsView;
        view.CustomSort = new FileEntryComparer("Name", true, true);
        var list = new System.Windows.Controls.ListView { ItemsSource = view, Width = 400, Height = 240 };
        var columns = new System.Windows.Controls.GridView();
        columns.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "Name", Width = 340,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(FileEntry.Name)) });
        list.View = columns;
        System.Windows.Controls.VirtualizingPanel.SetScrollUnit(list, System.Windows.Controls.ScrollUnit.Pixel);
        var host = new System.Windows.Window { Content = list, Width = 420, Height = 280, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); list.UpdateLayout();
            var type = typeof(FileEntry).Assembly.GetType("FileKakari.ListViewAsyncSortController")!;
            var controller = Activator.CreateInstance(type)!;
            var method = type.GetMethod("SortAsync")!;
            bool current = true;
            var resets = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)view).CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
            };
            Task<bool> Sort(bool ascending) => (Task<bool>)method.Invoke(controller, new object[]
            {
                list, state.Items, view, "Size", ascending, true, (Func<bool>)(() => current),
                (Action<Action>)(update => update()), state
            })!;
            var obsolete = Sort(true);
            var latest = Sort(false);
            list.SelectedItem = entries[2000];
            list.ScrollIntoView(entries[2000]); list.UpdateLayout();
            PumpDispatcherUntilComplete(Task.WhenAll(obsolete, latest));
            Require(!obsolete.Result && latest.Result, "Latest column click did not supersede old request.");
            Require(resets == 1 && view.CustomSort is null, "Sort must publish one Reset without a second WPF sort.");
            Require(view.Cast<FileEntry>().SequenceEqual(entries.Reverse()), "Published order differs from worker result.");
            Require(ReferenceEquals(list.SelectedItem, entries[2000]), "Sort restored the initial selection instead of the latest input.");
            var row = list.ItemContainerGenerator.ContainerFromItem(entries[2000]) as System.Windows.Controls.ListViewItem;
            Require(row is not null && row.TranslatePoint(new System.Windows.Point(), list).Y < list.ActualHeight,
                "Visible selected anchor was lost after sorting.");

            var filtered = Sort(true);
            view.Filter = item => ((FileEntry)item).Size % 2 == 0;
            PumpDispatcherUntilComplete(filtered);
            Require(filtered.Result && view.Cast<FileEntry>().SequenceEqual(entries.Where(e => e.Size % 2 == 0)),
                "Filter change during sorting was not retried against current data.");
            view.Filter = null;
            Require(view.Cast<FileEntry>().SequenceEqual(entries), "Removing the filter lost hidden rows or sort order.");
            var movedAway = Sort(false);
            current = false;
            PumpDispatcherUntilComplete(movedAway);
            Require(!movedAway.Result && view.Cast<FileEntry>().SequenceEqual(entries), "Old owner published into a different tab.");
            current = true;
            list.SelectedItem = null;
            list.ScrollIntoView(entries[1000]); list.UpdateLayout();
            System.Windows.Controls.ScrollViewer? FindSortScroll(System.Windows.DependencyObject parent)
            {
                if (parent is System.Windows.Controls.ScrollViewer scroll) return scroll;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    if (FindSortScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
                return null;
            }
            var sortScroll = FindSortScroll(list)!;
            var viewport = (System.Windows.Controls.ScrollContentPresenter)sortScroll.Template.FindName("PART_ScrollContentPresenter", sortScroll);
            var viewportTop = viewport.TranslatePoint(new System.Windows.Point(), list).Y;
            var firstVisible = entries.Select(entry => (Entry: entry,
                Row: list.ItemContainerGenerator.ContainerFromItem(entry) as System.Windows.Controls.ListViewItem))
                .Where(pair => pair.Row is not null)
                .Select(pair => (pair.Entry, Top: pair.Row!.TranslatePoint(new System.Windows.Point(), list).Y, Height: pair.Row.ActualHeight))
                .Where(pair => pair.Top + pair.Height > viewportTop && pair.Top < viewportTop + viewport.ActualHeight)
                .OrderBy(pair => pair.Top).First();
            var withoutSelection = Sort(false);
            PumpDispatcherUntilComplete(withoutSelection);
            var restoredRow = list.ItemContainerGenerator.ContainerFromItem(firstVisible.Entry) as System.Windows.Controls.ListViewItem;
            Require(withoutSelection.Result && restoredRow is not null
                && Math.Abs(restoredRow.TranslatePoint(new System.Windows.Point(), list).Y - firstVisible.Top) < 1,
                $"Sorting without selection did not keep the top visible row at its relative position: entry={firstVisible.Entry.Name}, before={firstVisible.Top}, after={restoredRow?.TranslatePoint(new System.Windows.Point(), list).Y}, viewport={viewportTop}.");
            var changedMetadata = Sort(true);
            entries[0].Size = 99999;
            PumpDispatcherUntilComplete(changedMetadata);
            Require(changedMetadata.Result && ReferenceEquals(view.GetItemAt(view.Count - 1), entries[0]),
                "Metadata change during snapshot construction was not included in the final order.");
        }
        finally { host.Close(); }
    }));

    await CheckAsync("snapshot-sort-matches-existing-columns", async () =>
    {
        var entries = Enumerable.Range(0, 257).Select(i => new FileEntry
        {
            Name = $"{(i % 2 == 0 ? "Ａ" : "a")}{i % 17:D2}.{(i % 3 == 0 ? "txt" : "bin")}",
            FullPath = Path.Combine(fixtures, i.ToString(), $"file{i}.txt"), Kind = $"kind{i % 3}",
            IsDirectory = i % 11 == 0, Size = i % 7 == 0 ? null : i % 13,
            ModifiedAt = new DateTime(2020, 1, 1).AddDays(i % 9),
            CreatedAt = new DateTime(2021, 1, 1).AddDays(i % 7),
            AccessedAt = new DateTime(2022, 1, 1).AddDays(i % 5)
        }).Reverse().ToArray();
        foreach (var column in new[] { "Name", "Kind", "Size", "ModifiedAt", "CreatedAt", "AccessedAt", "Extension", "Attributes", "FullPath", "ParentPath", "BaseName" })
        foreach (var ascending in new[] { true, false })
        foreach (var foldersFirst in new[] { true, false })
        foreach (var preserveTies in new[] { true, false })
        {
            var previous = preserveTies ? entries.Select((entry, i) => (entry.FullPath, i)).ToDictionary(pair => pair.FullPath, pair => pair.i) : null;
            var comparer = new FileEntryComparer(column, ascending, foldersFirst, previous);
            var expected = entries.OrderBy(entry => entry, Comparer<FileEntry>.Create(comparer.Compare)).ToArray();
            var keys = entries.Select((entry, i) => FileSortSnapshot.Capture(entry, i, column, preserveTies ? i : null)).ToArray();
            var result = await FileSortSnapshot.SortAsync(keys, column, ascending, foldersFirst, CancellationToken.None);
            Require(result.Select(i => entries[i]).SequenceEqual(expected), $"Snapshot order differs: {column}/{ascending}/{foldersFirst}/{preserveTies}");
        }
        var snapshot = entries.Select((entry, i) => FileSortSnapshot.Capture(entry, i, "Size", i)).ToArray();
        var before = await FileSortSnapshot.SortAsync(snapshot, "Size", true, true, CancellationToken.None);
        foreach (var entry in entries) entry.Size = 999;
        Require((await FileSortSnapshot.SortAsync(snapshot, "Size", true, true, CancellationToken.None)).SequenceEqual(before), "Worker snapshot read changed metadata.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await FileSortSnapshot.SortAsync(snapshot, "Size", true, true, cancelled.Token); throw new InvalidOperationException("Cancelled sort completed."); }
        catch (OperationCanceledException) { }
    });

    await CheckAsync("pane-column-sort-preserves-group-policy", () => RunOnStaAsync(() =>
    {
        var entries = new[] { ("a.txt", 30), ("b.bin", 20), ("c.txt", 10), ("d.bin", 40) }.Select(pair =>
        {
            var path = Path.Combine(fixtures, pair.Item1);
            File.WriteAllBytes(path, new byte[pair.Item2]);
            return FileEntry.FromFileBasic(new FileInfo(path));
        }).ToArray();
        var list = new FileListState();
        list.ReplaceItems(fixtures, entries);
        list.ItemsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FileEntry.Extension)));
        var state = new WorkspaceTabState(fixtures) { SortColumn = "Size", SortAscending = true, GroupMode = GroupMode.Extension };
        list.ApplySort(state, true);
        Require(((System.Windows.Data.ListCollectionView)list.ItemsView).CustomSort!.Compare(entries[0], entries[1]) < 0,
            "Comparer used individual size instead of the group's representative size.");
        Require(list.ItemsView.Cast<FileEntry>().Select(item => item.Name).SequenceEqual(new[] { "c.txt", "a.txt", "b.bin", "d.bin" }),
            "Ascending column sort did not keep groups ordered by their representatives.");
        state.SortAscending = false;
        list.ApplySort(state, true);
        Require(list.ItemsView.Cast<FileEntry>().Select(item => item.Name).SequenceEqual(new[] { "d.bin", "b.bin", "a.txt", "c.txt" }),
            "Descending column sort lost group ordering.");
    }));

    await CheckAsync("shared-icon-request-independent-cancellation", async () =>
    {
        var cache = new ShellIconCache(2);
        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        CancellationToken sharedToken = default;
        Task<ImageSource?> Load(CancellationToken token)
        {
            loads++;
            sharedToken = token;
            return completion.Task;
        }
        using var firstCancellation = new CancellationTokenSource();
        var first = cache.GetAsync(".TXT", Load, firstCancellation.Token);
        var second = cache.GetAsync(".txt", Load);
        Require(loads == 1, "Concurrent equivalent icon keys started duplicate loads.");
        firstCancellation.Cancel();
        try { await first; throw new InvalidOperationException("Recycled row did not cancel its wait."); }
        catch (OperationCanceledException) { }
        Require(!sharedToken.IsCancellationRequested && !second.IsCompleted, "One recycled row cancelled another pane's icon.");
        var icon = new DrawingImage();
        icon.Freeze();
        completion.SetResult(icon);
        Require(ReferenceEquals(await second.WaitAsync(TimeSpan.FromSeconds(5)), icon), "Remaining caller lost shared result.");
        Require(ReferenceEquals(await cache.GetAsync(".txt", Load), icon) && loads == 1, "Completed icon was not reused.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await cache.GetAsync(".txt", Load, cancelled.Token); throw new InvalidOperationException("Cache hit ignored cancellation."); }
        catch (OperationCanceledException) { }
    });

    await CheckAsync("icon-cache-lru-bound-and-retry", async () =>
    {
        var cache = new ShellIconCache(2);
        var loads = 0;
        Task<ImageSource?> Load(CancellationToken _)
        {
            loads++;
            var image = new DrawingImage();
            image.Freeze();
            return Task.FromResult<ImageSource?>(image);
        }
        var a = await cache.GetAsync("a", Load);
        await cache.GetAsync("b", Load);
        Require(ReferenceEquals(await cache.GetAsync("A", Load), a), "Cache hit did not reuse image.");
        await cache.GetAsync("c", Load);
        Require(ReferenceEquals(await cache.GetAsync("a", Load), a) && loads == 3, "Recently used image was evicted.");
        await cache.GetAsync("b", Load);
        Require(loads == 4, "Least recently used image exceeded capacity without eviction.");
        Require(await cache.GetAsync("missing", _ => Task.FromResult<ImageSource?>(null)) is null, "Null icon was changed.");
        Require(await cache.GetAsync("missing", Load) is not null, "Failed native icon became a permanent cache entry.");
        try { await cache.GetAsync("error", _ => Task.FromException<ImageSource?>(new IOException("test"))); }
        catch (IOException) { }
        Require(await cache.GetAsync("error", Load) is not null, "Failed request could not retry.");
    });

    await CheckAsync("abandoned-icon-request-cannot-replace-new-request", async () =>
    {
        var cache = new ShellIconCache(2);
        // Inline completion lets the obsolete load finish before checking the new request.
        var oldCompletion = new TaskCompletionSource<ImageSource?>();
        CancellationToken sharedToken = default;
        using var cancellation = new CancellationTokenSource();
        var abandoned = cache.GetAsync("same.exe", token => { sharedToken = token; return oldCompletion.Task; }, cancellation.Token);
        cancellation.Cancel();
        try { await abandoned; throw new InvalidOperationException("Abandoned request kept waiting."); }
        catch (OperationCanceledException) { }
        Require(sharedToken.IsCancellationRequested, "No remaining callers, but queued work was not cancelled.");
        var freshCompletion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = cache.GetAsync("same.exe", _ => freshCompletion.Task);
        var oldIcon = new DrawingImage(); oldIcon.Freeze();
        oldCompletion.SetResult(oldIcon);
        var follower = cache.GetAsync("same.exe", _ => throw new InvalidOperationException("Old completion removed new request"));
        var currentIcon = new DrawingImage(); currentIcon.Freeze();
        freshCompletion.SetResult(currentIcon);
        Require(ReferenceEquals(await fresh.WaitAsync(TimeSpan.FromSeconds(5)), currentIcon), "New request received abandoned result.");
        Require(ReferenceEquals(await follower.WaitAsync(TimeSpan.FromSeconds(5)), currentIcon), "New shared request was lost.");
        Require(ReferenceEquals(await cache.GetAsync("same.exe", _ => throw new InvalidOperationException("Cache entry lost")), currentIcon),
            "Abandoned request replaced current icon.");
    });

    await CheckAsync("selection-followup-batch-isolation-and-lifetime", () => RunOnStaAsync(() =>
    {
        var left = new System.Windows.Controls.ListView();
        var right = new System.Windows.Controls.ListView();
        var leftUpdates = 0;
        var rightUpdates = 0;
        var last = 0;
        var outer = SelectionUpdateBatch.Begin(left);
        using (SelectionUpdateBatch.Begin(left))
        {
            for (var i = 0; i < 100; i++)
            {
                var value = i;
                SelectionUpdateBatch.RunOrDefer(left, () => { leftUpdates++; last = value; });
            }
            SelectionUpdateBatch.RunOrDefer(right, () => rightUpdates++);
        }
        Require(leftUpdates == 0 && rightUpdates == 1, "Nested batching delayed another pane or flushed too early.");
        outer.Dispose();
        outer.Dispose();
        Require(leftUpdates == 1 && last == 99, "Final update was lost, duplicated, or used intermediate state.");
        using (SelectionUpdateBatch.Begin(left)) { }
        Require(leftUpdates == 1, "No-op selection repeated a pending update.");
        try
        {
            using var failing = SelectionUpdateBatch.Begin(left);
            SelectionUpdateBatch.RunOrDefer(left, () => leftUpdates++);
            throw new InvalidOperationException("test operation failure");
        }
        catch (InvalidOperationException) { }
        Require(leftUpdates == 2, "Partial selection was not committed on scope exit.");
        SelectionUpdateBatch.RunOrDefer(left, () => leftUpdates++);
        Require(leftUpdates == 3, "Failed operation left selection updates suppressed.");
    }));

    await CheckAsync("preview-host-parent-dpi-and-native-resize", () => RunOnStaAsync(PreviewHostDpiChecks.Run));

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
                0L, (Func<bool>)(() => stale), false })!;
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
        var followups = 0;
        var finalSelection = new HashSet<FileEntry>();
        list.SelectionChanged += (_, _) => SelectionUpdateBatch.RunOrDefer(list, () =>
        {
            followups++;
            finalSelection = list.SelectedItems.Cast<FileEntry>().ToHashSet();
        });
        Shift(entries[0], entries[199]);
        Expect(entries.Take(200));
        Require(followups == 1 && finalSelection.SetEquals(entries.Take(200)), "Large Shift selection did not publish one complete final state synchronously.");
        added = removed = 0;
        Shift(entries[0], entries[199]);
        Require(added == 0 && removed == 0, "Unchanged Shift range reselected rows.");
        Require(followups == 1, "Unchanged range repeated follow-up work.");
        Shift(entries[0], entries[209]);
        Require(added == 10 && removed == 0, "Growing range changed retained selections.");
        Require(followups == 2 && finalSelection.Count == 210, "Growing range ran per-item follow-ups.");
        Shift(entries[29], entries[10]);
        Expect(entries.Skip(10).Take(20));
        Require(followups == 3 && finalSelection.SetEquals(entries.Skip(10).Take(20)), "Shrinking range exposed intermediate selection.");
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

    await CheckAsync("group-cache-reuse-invalidation-and-list-isolation", () => RunOnStaAsync(() =>
    {
        var folder = Path.Combine(fixtures, "group-cache");
        Directory.CreateDirectory(folder);
        var entries = new List<FileEntry>();
        foreach (var name in new[] { "Series 01.txt", "Series 02.txt", "Other.pdf" })
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "x");
            entries.Add(FileEntry.FromFile(new FileInfo(path)));
        }
        var cache = new FileListGroupCache();
        var similar = cache.EnsureSimilarNames(entries);
        var first = cache.EnsureRepresentatives(entries, GroupMode.SimilarName, "Size", true, true);
        Require(ReferenceEquals(first, cache.EnsureRepresentatives(entries, GroupMode.SimilarName, "Size", true, true))
            && cache.LastRepresentativeCacheHit, "Unchanged preparation did not reuse its representative index.");
        Require(ReferenceEquals(similar, cache.EnsureSimilarNames(entries)), "Unchanged classification was rebuilt.");
        var descending = cache.EnsureRepresentatives(entries, GroupMode.SimilarName, "Size", false, true);
        Require(!ReferenceEquals(first, descending), "Direction change reused representatives.");
        Require(ReferenceEquals(similar, cache.EnsureSimilarNames(entries)), "Sort change rebuilt classification.");
        entries[1].Size = 999;
        var resized = cache.EnsureRepresentatives(entries, GroupMode.SimilarName, "Size", false, true);
        Require(!ReferenceEquals(descending, resized), "Metadata change reused representatives.");
        Require(ReferenceEquals(resized.GetRepresentative(cache.SimilarNameIndex!.GetGroupKey(entries[1])), entries[1]),
            "Changed size did not choose the new representative.");
        entries[1].Name = "Different.txt";
        var renamed = cache.EnsureSimilarNames(entries);
        Require(!ReferenceEquals(similar, renamed) && renamed.GetGroupKey(entries[1]) == SimilarNameGroupIndex.Build(entries).GetGroupKey(entries[1]),
            "Rename kept stale classification.");
        var previousIndex = cache.EnsureRepresentatives(entries, GroupMode.Extension, "Name", true, true);
        foreach (var mutate in new Action[]
        {
            () => entries[0].FullPath += ".renamed",
            () => entries[0].ModifiedAt = entries[0].ModifiedAt.AddDays(-10),
            () => entries[0].CreatedAt = entries[0].CreatedAt.AddDays(-10),
            () => entries[0].AccessedAt = entries[0].AccessedAt.AddDays(-10),
            () => entries[0].Kind = "changed-kind",
            () => entries.Reverse(),
            () => entries[0] = FileEntry.FromFile(new FileInfo(Path.Combine(folder, "Other.pdf")))
        })
        {
            mutate();
            var next = cache.EnsureRepresentatives(entries, GroupMode.Extension, "Name", true, true);
            Require(!ReferenceEquals(previousIndex, next), "Entry mutation, replacement or reordering reused a stale index.");
            previousIndex = next;
        }
        Require(!ReferenceEquals(previousIndex, cache.EnsureRepresentatives(entries, GroupMode.Extension, "Name", true, false)),
            "Folder-priority change reused the previous configuration.");
        foreach (var mode in new[] { GroupMode.Extension, GroupMode.Date, GroupMode.SimilarName })
        foreach (var column in new[] { "Name", "Size", "ModifiedAt", "CreatedAt", "AccessedAt", "Kind" })
        foreach (var ascending in new[] { true, false })
        {
            var actual = cache.EnsureRepresentatives(entries, mode, column, ascending, true);
            var names = mode == GroupMode.SimilarName ? cache.SimilarNameIndex : null;
            var expected = GroupRepresentativeIndex.Build(entries, mode, column, ascending, true, names);
            var comparer = new FileEntryComparer(column, ascending, true, groupMode: mode, groupIndex: names, representativeIndex: actual);
            var baseline = new FileEntryComparer(column, ascending, true, groupMode: mode, groupIndex: names, representativeIndex: expected);
            foreach (var a in entries) foreach (var b in entries)
                Require(comparer.Compare(a, b) == baseline.Compare(a, b), "Cached group ordering differs from a fresh build.");
        }
        var state = new FileListState();
        state.ReplaceItems(folder, entries);
        var before = state.EnsureSimilarNameIndex();
        state.Items.RemoveAt(0);
        Require(state.SimilarNameIndex is null, "Direct collection mutation retained the old cache.");
        Require(!ReferenceEquals(before, state.EnsureSimilarNameIndex()), "Changed collection reused classification.");
        var other = new FileListState();
        other.ReplaceItems(folder, entries);
        Require(!ReferenceEquals(other.EnsureSimilarNameIndex(), state.EnsureSimilarNameIndex()), "Panes shared cache ownership.");
        state.Items.Clear();
        Require(state.SimilarNameIndex is null, "Empty list retained classification.");
        cache.Clear();
        Require(cache.SimilarNameIndex is null && !cache.LastRepresentativeCacheHit, "Explicit clear retained cached data.");
    }));

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
    await CheckAsync("image-zoom-anchor-pan-fit-and-resize", () =>
    {
        var transform = new ImageViewportTransform();
        transform.SetGeometry(2000, 1000, 500, 400);
        Require(transform.IsFit && Math.Abs(transform.Scale - .25) < .001 && Math.Abs(transform.Y - 75) < .001, "Fit geometry is incorrect.");
        var imageX = (200 - transform.X) / transform.Scale;
        var imageY = (150 - transform.Y) / transform.Scale;
        transform.ZoomAt(1, 200, 150);
        Require(Math.Abs((200 - transform.X) / transform.Scale - imageX) < .001
            && Math.Abs((150 - transform.Y) / transform.Scale - imageY) < .001, "Pointer anchor moved during zoom.");
        transform.Pan(99999, -99999);
        Require(transform.X == 0 && transform.Y == -600, "Panning exposed space beyond image bounds.");
        transform.SetGeometry(2000, 1000, 800, 600);
        Require(transform.Scale == 1 && !transform.IsFit && transform.Y >= -400, "Resize lost zoom or pan bounds.");
        transform.ZoomAt(999, 400, 300);
        Require(transform.Scale == 8, "Maximum zoom was not bounded.");
        transform.Fit();
        Require(transform.IsFit && Math.Abs(transform.Scale - .4) < .001, "Fit did not reset zoom.");
        transform.SetGeometry(100, 50, 800, 600);
        Require(transform.Scale == 1, "Fit enlarged a small original.");
        return Task.CompletedTask;
    });

    await CheckAsync("image-tiles-independent-load-and-disposal", () => RunOnStaAsync(() =>
    {
        var path = Path.Combine(fixtures, "tile.png");
        var pixels = new byte[800 * 400 * 4];
        for (var y = 0; y < 400; y++)
        for (var x = 0; x < 800; x++)
        {
            var offset = (y * 800 + x) * 4;
            pixels[offset] = (byte)(x * 255 / 800);
            pixels[offset + 1] = (byte)(y * 255 / 400);
            pixels[offset + 2] = (byte)((x / 80 + y / 80) % 2 == 0 ? 210 : 60);
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(800, 400, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2 };
        var tiles = Enumerable.Range(0, 4).Select(i => new ImagePreviewTile(i == 3 ? Path.Combine(fixtures, "absent.png") : path, true, CancellationToken.None)).ToArray();
        foreach (var tile in tiles) grid.Children.Add(tile);
        var host = new System.Windows.Window { Content = grid, Width = 640, Height = 480, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        foreach (var theme in new[] { "ThemeResources.xaml", "LightTheme.xaml" })
        {
            using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Themes", theme));
            host.Resources.MergedDictionaries.Add((System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(file));
        }
        try
        {
            host.Show(); grid.UpdateLayout();
            PumpDispatcherUntilComplete(Task.WhenAll(tiles.Select(tile => tile.LoadAsync())));
            Require(tiles.Take(3).All(tile => tile.Result?.ImageSource is { IsFrozen: true, PixelWidth: < 400 }), "Tiles did not decode independently at viewport size.");
            Require(tiles[3].Result is null, "Missing tile should not stop the other images.");
            var otherZoom = tiles[1].ZoomScale;
            tiles[0].ZoomAt(1, new System.Windows.Point(100, 80));
            Require(tiles[0].ZoomScale == 1 && !tiles[0].IsFit && tiles[1].ZoomScale == otherZoom && tiles[1].IsFit,
                "Zoom affected a different image.");
            tiles[0].SetActive(true);
            grid.UpdateLayout();
            var render = new RenderTargetBitmap((int)grid.ActualWidth, (int)grid.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(grid);
            var screenshot = new PngBitmapEncoder();
            screenshot.Frames.Add(BitmapFrame.Create(render));
            using (var file = File.Create(Path.Combine(runDirectory, "image-tiles.png"))) screenshot.Save(file);
            var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 120)
                { RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent };
            tiles[0].ZoomWheel(wheel);
            Require(wheel.Handled && tiles[0].ZoomScale > 1, "Image zoom wheel was not consumed.");
            tiles[0].Fit();
            Require(tiles[0].IsFit && tiles[0].ZoomScale < 1, "Fit button did not restore whole image.");
            tiles[0].Dispose();
            Require(tiles[0].Result is null && tiles[1].Result is not null, "Disposing one tile affected another tile.");
            using var cancelled = new CancellationTokenSource();
            var obsolete = new ImagePreviewTile(path, false, cancelled.Token);
            cancelled.Cancel();
            PumpDispatcherUntilComplete(obsolete.LoadAsync());
            Require(obsolete.Result is null, "Cancelled tile displayed a stale result.");
            obsolete.Dispose();
        }
        finally { foreach (var tile in tiles) tile.Dispose(); host.Close(); }
    }));

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
        var decodeGate = (SemaphoreSlim)typeof(BuiltInImagePreviewProvider)
            .GetField("DecodeGate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        await decodeGate.WaitAsync();
        try
        {
            using var resizeCancellation = new CancellationTokenSource();
            var resize = provider.CreatePreviewAsync(request with { TargetHeightDip = 480 }, resizeCancellation.Token);
            var hits = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(_ => provider.CreatePreviewAsync(request, CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(5));
            Require(hits.All(hit => ReferenceEquals(hit.ImageSource, first.ImageSource)),
                "Cached image waited for the decoder or was evicted by a pending resize.");
            Require(!resize.IsCompleted, "An uncached larger image bypassed the decode concurrency limit.");
            resizeCancellation.Cancel();
            try { await resize; throw new InvalidOperationException("Cancelled resize completed."); }
            catch (OperationCanceledException) { }
            using var cancelledHit = new CancellationTokenSource();
            cancelledHit.Cancel();
            try { await provider.CreatePreviewAsync(request, cancelledHit.Token); throw new InvalidOperationException("Cancelled cache hit completed."); }
            catch (OperationCanceledException) { }
        }
        finally { decodeGate.Release(); }
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
