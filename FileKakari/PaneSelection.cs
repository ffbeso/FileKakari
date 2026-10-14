using System.Windows;

namespace FileKakari;

public static class PaneSelection
{
    // Null preserves the usual keyboard-focus selection colors outside folder panes.
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool?), typeof(PaneSelection),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static bool? GetIsActive(DependencyObject element) => (bool?)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool? value) => element.SetValue(IsActiveProperty, value);
}
