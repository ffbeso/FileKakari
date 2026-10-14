using System.Windows;

namespace FileKakari;

/// <summary>Loads icons only for realized templates; recycling cancels queued work.</summary>
public static class FileIconLoader
{
    private static readonly bool IconsEnabled = DevListPerfOptions.FromEnvironment().ShellIconsEnabled;
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FileIconLoader), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty CancellationProperty = DependencyProperty.RegisterAttached(
        "Cancellation", typeof(CancellationTokenSource), typeof(FileIconLoader));

    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)args.NewValue && IconsEnabled)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.DataContextChanged += OnDataContextChanged;
            if (element.IsLoaded) Load(element);
        }
        else
        {
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
            element.DataContextChanged -= OnDataContextChanged;
            Cancel(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Load((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs args) => Cancel((FrameworkElement)sender);
    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        Cancel(element);
        if (element.IsLoaded) Load(element);
    }

    private static async void Load(FrameworkElement element)
    {
        Cancel(element);
        if (element.DataContext is not FileEntry entry || entry.Icon is not null) return;
        var cancellation = new CancellationTokenSource();
        element.SetValue(CancellationProperty, cancellation);
        try { await entry.LoadIconAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(element.GetValue(CancellationProperty), cancellation))
                element.ClearValue(CancellationProperty);
            cancellation.Dispose();
        }
    }

    private static void Cancel(FrameworkElement element)
    {
        if (element.GetValue(CancellationProperty) is CancellationTokenSource cancellation)
        {
            element.ClearValue(CancellationProperty);
            cancellation.Cancel();
        }
    }
}
