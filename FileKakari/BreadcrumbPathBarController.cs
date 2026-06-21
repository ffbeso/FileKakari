using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileKakari;

internal sealed class BreadcrumbPathBarController
{
    private const int MaxBreadcrumbSegments = 6;
    private const int BreadcrumbTailSegments = 3;

    private readonly StackPanel _panel;
    private readonly ScrollViewer _breadcrumbScroller;
    private readonly TextBox _pathBox;
    private readonly LocalizationService _text;
    private readonly Func<string> _getCurrentPath;
    private readonly Func<string, Task> _navigateFromBreadcrumbAsync;
    private readonly Func<string, Task> _navigateFromPathInputAsync;
    private readonly Action<TextBox> _focusAndSelectTextBox;
    private readonly Action<string> _showPathNotFound;
    private readonly Action<Button> _configureBreadcrumbButton;

    public BreadcrumbPathBarController(
        StackPanel panel,
        ScrollViewer breadcrumbScroller,
        TextBox pathBox,
        LocalizationService text,
        Func<string> getCurrentPath,
        Func<string, Task> navigateFromBreadcrumbAsync,
        Func<string, Task> navigateFromPathInputAsync,
        Action<TextBox> focusAndSelectTextBox,
        Action<string> showPathNotFound,
        Action<Button> configureBreadcrumbButton)
    {
        _panel = panel;
        _breadcrumbScroller = breadcrumbScroller;
        _pathBox = pathBox;
        _text = text;
        _getCurrentPath = getCurrentPath;
        _navigateFromBreadcrumbAsync = navigateFromBreadcrumbAsync;
        _navigateFromPathInputAsync = navigateFromPathInputAsync;
        _focusAndSelectTextBox = focusAndSelectTextBox;
        _showPathNotFound = showPathNotFound;
        _configureBreadcrumbButton = configureBreadcrumbButton;
    }

    public bool IsEditing { get; private set; }

    public void Update(string path)
    {
        _pathBox.Text = path;
        UpdateBreadcrumb(path);
        if (!IsEditing)
        {
            ShowBreadcrumbPathBar();
        }
    }

    public void BeginEdit()
    {
        if (IsEditing)
        {
            _focusAndSelectTextBox(_pathBox);
            return;
        }

        IsEditing = true;
        _pathBox.Text = _getCurrentPath();
        _breadcrumbScroller.Visibility = Visibility.Collapsed;
        _pathBox.Visibility = Visibility.Visible;
        _focusAndSelectTextBox(_pathBox);
    }

    public void CancelEdit()
    {
        Update(_getCurrentPath());
        ShowBreadcrumbPathBar();
    }

    public void ShowBreadcrumbPathBar()
    {
        IsEditing = false;
        _pathBox.Visibility = Visibility.Collapsed;
        _breadcrumbScroller.Visibility = Visibility.Visible;
    }

    public async Task NavigateFromPathBoxAsync()
    {
        var rawPath = _pathBox.Text;
        var normalizedPath = NormalizePathBarInput(rawPath);
        if (normalizedPath is null)
        {
            _showPathNotFound(rawPath);
            CancelEdit();
            return;
        }

        await _navigateFromPathInputAsync(normalizedPath);
        ShowBreadcrumbPathBar();
    }

    private void UpdateBreadcrumb(string path)
    {
        _panel.Children.Clear();
        if (SpecialLocationService.IsSpecialUri(path))
        {
            AddButton(_text.Get("LocationThisPc"), SpecialLocationService.ThisPcUri, isCurrent: true);
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                AddButton(fullPath, fullPath, isCurrent: true);
                return;
            }

            var normalizedRoot = EnsureDirectorySeparator(root);
            var segments = new List<BreadcrumbSegment>
            {
                new(_text.Get("LocationThisPc"), SpecialLocationService.ThisPcUri, false),
                new(
                    FormatRoot(root),
                    normalizedRoot,
                    string.Equals(EnsureDirectorySeparator(fullPath), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            };

            var relative = Path.GetRelativePath(normalizedRoot, fullPath);
            if (!string.IsNullOrWhiteSpace(relative) && relative != ".")
            {
                var current = normalizedRoot;
                foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    segments.Add(new BreadcrumbSegment(
                        segment,
                        current,
                        string.Equals(Path.GetFullPath(current), fullPath, StringComparison.OrdinalIgnoreCase)));
                }
            }

            AddSegments(segments);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _panel.Children.Clear();
            AddButton(path, path, isCurrent: true);
        }
    }

    private void AddSegments(IReadOnlyList<BreadcrumbSegment> segments)
    {
        if (segments.Count <= MaxBreadcrumbSegments)
        {
            foreach (var segment in segments)
            {
                AddSeparatorIfNeeded();
                AddButton(segment.Text, segment.TargetPath, segment.IsCurrent);
            }

            return;
        }

        var rootSegment = segments[0];
        AddSeparatorIfNeeded();
        AddButton(rootSegment.Text, rootSegment.TargetPath, rootSegment.IsCurrent);
        AddSeparator();
        AddEllipsis();

        foreach (var segment in segments.Skip(Math.Max(1, segments.Count - BreadcrumbTailSegments)))
        {
            AddSeparator();
            AddButton(segment.Text, segment.TargetPath, segment.IsCurrent);
        }
    }

    private void AddSeparatorIfNeeded()
    {
        if (_panel.Children.Count > 0)
        {
            AddSeparator();
        }
    }

    private void AddButton(string text, string targetPath, bool isCurrent)
    {
        var button = new Button
        {
            Content = text,
            Tag = targetPath,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(3, 0, 3, 0),
            MinHeight = 22,
            Margin = new Thickness(0, 0, 1, 0)
        };
        button.SetResourceReference(Control.ForegroundProperty, isCurrent ? "SubtleTextBrush" : "TextBrush");
        button.Click += BreadcrumbButton_Click;
        _configureBreadcrumbButton(button);
        _panel.Children.Add(button);
    }

    private void AddSeparator()
    {
        var separator = new TextBlock
        {
            Text = ">",
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        separator.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        _panel.Children.Add(separator);
    }

    private void AddEllipsis()
    {
        var button = new Button
        {
            Content = "...",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(3, 0, 3, 0),
            MinHeight = 22,
            Margin = new Thickness(0, 0, 1, 0)
        };
        button.SetResourceReference(Control.ForegroundProperty, "SubtleTextBrush");
        button.Click += (_, _) => BeginEdit();
        _panel.Children.Add(button);
    }

    private async void BreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }
            || NormalizeTargetPath(path) is not { } targetPath)
        {
            return;
        }

        await _navigateFromBreadcrumbAsync(targetPath);
    }

    private static string? NormalizePathBarInput(string input)
    {
        var trimmed = input.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (SpecialLocationService.IsSpecialUri(trimmed)
            || string.Equals(trimmed, "This PC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "PC", StringComparison.OrdinalIgnoreCase))
        {
            return SpecialLocationService.ThisPcUri;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            return Path.IsPathFullyQualified(expanded)
                ? Path.GetFullPath(expanded)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizeTargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (SpecialLocationService.IsSpecialUri(trimmed))
        {
            return SpecialLocationService.ThisPcUri;
        }

        try
        {
            var fullPath = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root)
                && string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    ? EnsureDirectorySeparator(root)
                    : fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string FormatRoot(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(trimmed) ? root : trimmed;
    }

    private static string EnsureDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private sealed record BreadcrumbSegment(string Text, string TargetPath, bool IsCurrent);
}
