using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileKakari;

internal static class FileListScrollDiagnostics
{
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(FileListScrollDiagnostics), new PropertyMetadata(false));

    public static void Attach(ListView list)
    {
        if (Environment.GetEnvironmentVariable("FILEKAKARI_SCROLL_DIAG") != "1"
            || (bool)list.GetValue(AttachedProperty)) return;
        list.SetValue(AttachedProperty, true);
        string lastInput = "none";
        var lastInputAt = Environment.TickCount64;
        void Log(string kind, string detail)
        {
            var pane = list.DataContext as FolderPane;
            PerfLog.Write($"scroll-diag kind={kind} list={list.GetHashCode()} pane={pane?.Id} " +
                $"state={pane?.ActiveTabState?.Id} generation={pane?.FileList.LoadGeneration} " +
                $"suppressed={pane?.FileList.ViewStateSyncSuppression.IsActive} " +
                $"count={list.Items.Count} selectedIndex={list.SelectedIndex} selectedCount={list.SelectedItems.Count} " +
                $"focus={Keyboard.FocusedElement?.GetType().Name} input={lastInput} inputAgeMs={Environment.TickCount64 - lastInputAt} {detail}");
        }
        list.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            lastInput = $"key:{e.Key}:{Keyboard.Modifiers}";
            lastInputAt = Environment.TickCount64;
            Log("input", $"handled={e.Handled}");
        }), true);
        list.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            lastInput = $"mouse:{e.ChangedButton}";
            lastInputAt = Environment.TickCount64;
            Log("input", "");
        }), true);
        list.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            lastInput = $"wheel:{e.Delta}";
            lastInputAt = Environment.TickCount64;
        }), true);
        list.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
        {
            var index = e.TargetObject is ListViewItem row ? list.ItemContainerGenerator.IndexFromContainer(row) : -1;
            Log("bring", $"target={e.TargetObject?.GetType().Name} targetIndex={index} handled={e.Handled}");
        }), true);
        list.SelectionChanged += (_, e) => Log("selection", $"added={e.AddedItems.Count} removed={e.RemovedItems.Count}");
        list.GotKeyboardFocus += (_, _) => Log("focus", "");
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (Math.Abs(e.VerticalChange) < Math.Max(1, e.ViewportHeight)
                && !(e.VerticalChange > 0 && e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1)) return;
            Log("jump", $"offset={e.VerticalOffset:F1} delta={e.VerticalChange:F1} extent={e.ExtentHeight:F1} " +
                $"extentDelta={e.ExtentHeightChange:F1} viewport={e.ViewportHeight:F1}");
        }), true);
    }
}
