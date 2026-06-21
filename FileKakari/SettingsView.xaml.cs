using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileKakari;

public partial class SettingsView : UserControl
{
    private readonly LocalizationService _text;

    public SettingsView(AppSettings settings, LocalizationService text)
    {
        InitializeComponent();
        _text = text;
        Result = settings.Clone();
        ApplyLocalizedText();
        LoadSettings();
    }

    public event EventHandler? SaveRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler<OpenFolderRequestedEventArgs>? OpenFolderRequested;

    public AppSettings Result { get; }

    private void ApplyLocalizedText()
    {
        ThemeLabel.Text = _text.Get("SettingsTheme");
        LanguageLabel.Text = _text.Get("SettingsLanguage");
        ShowHiddenFilesCheckBox.Content = _text.Get("SettingsShowHiddenFiles");
        ShowSystemFilesCheckBox.Content = _text.Get("SettingsShowSystemFiles");
        SortFoldersFirstCheckBox.Content = _text.Get("SettingsSortFoldersFirst");
        PreviewPanePlacementLabel.Text = _text.Get("SettingsPreviewPanePlacement");
        AutoPlayVideoPreviewCheckBox.Content = _text.Get("SettingsAutoPlayVideoPreview");
        AutoPlayVideoPreviewDescriptionText.Text = _text.Get("SettingsAutoPlayVideoPreviewDescription");
        FontFamilyLabel.Text = _text.Get("SettingsFontFamily");
        FontSizeLabel.Text = _text.Get("SettingsFontSize");
        RowHeightLabel.Text = _text.Get("SettingsRowHeight");
        ColumnsLabel.Text = _text.Get("SettingsColumns");
        ColumnNameCheckBox.Content = _text.Get("ColumnName");
        ColumnModifiedAtCheckBox.Content = _text.Get("ColumnModified");
        ColumnKindCheckBox.Content = _text.Get("ColumnType");
        ColumnSizeCheckBox.Content = _text.Get("ColumnSize");
        ColumnExtensionCheckBox.Content = _text.Get("ColumnExtension");
        ColumnCreatedAtCheckBox.Content = _text.Get("ColumnCreated");
        ColumnAccessedAtCheckBox.Content = _text.Get("ColumnAccessed");
        ColumnAttributesCheckBox.Content = _text.Get("ColumnAttributes");
        ColumnFullPathCheckBox.Content = _text.Get("ColumnFullPath");
        ColumnParentPathCheckBox.Content = _text.Get("ColumnParentPath");
        ColumnBaseNameCheckBox.Content = _text.Get("ColumnBaseName");
        OpenSettingsFolderButton.Content = _text.Get("SettingsOpenFolder");
        OpenThemesFolderButton.Content = _text.Get("SettingsOpenThemesFolder");
        SaveButton.Content = _text.Get("SettingsSave");
        CancelButton.Content = _text.Get("SettingsCancel");

        var themeChoices = new List<SettingsChoice<ThemeSelection>>
        {
            new(_text.Get("SettingsFollowSystem"), new ThemeSelection(AppThemeMode.System)),
            new(_text.Get("SettingsLight"), new ThemeSelection(AppThemeMode.Light)),
            new(_text.Get("SettingsDark"), new ThemeSelection(AppThemeMode.Dark))
        };
        foreach (var theme in ThemeManager.DiscoverCustomThemes())
        {
            themeChoices.Add(new SettingsChoice<ThemeSelection>(
                theme.DisplayName,
                new ThemeSelection(AppThemeMode.Custom, theme.FileName)));
        }

        ThemeComboBox.ItemsSource = themeChoices;
        LanguageComboBox.ItemsSource = new[]
        {
            new SettingsChoice<AppLanguageMode>(_text.Get("SettingsFollowSystem"), AppLanguageMode.System),
            new SettingsChoice<AppLanguageMode>(_text.Get("SettingsJapanese"), AppLanguageMode.Japanese),
            new SettingsChoice<AppLanguageMode>(_text.Get("SettingsEnglish"), AppLanguageMode.English)
        };
        PreviewPanePlacementComboBox.ItemsSource = new[]
        {
            new SettingsChoice<PreviewPanePlacement>(_text.Get("SettingsPreviewPanePlacementRight"), PreviewPanePlacement.Right),
            new SettingsChoice<PreviewPanePlacement>(_text.Get("SettingsPreviewPanePlacementBottom"), PreviewPanePlacement.Bottom)
        };
        FontFamilyComboBox.ItemsSource = GetFontFamilyChoices();
    }

    private void LoadSettings()
    {
        ThemeComboBox.SelectedValue = FindThemeSelection(Result.Theme, Result.CustomThemeName);
        LanguageComboBox.SelectedValue = Result.Language;
        ShowHiddenFilesCheckBox.IsChecked = Result.ShowHiddenFiles;
        ShowSystemFilesCheckBox.IsChecked = Result.ShowSystemFiles;
        SortFoldersFirstCheckBox.IsChecked = Result.SortFoldersFirst;
        PreviewPanePlacementComboBox.SelectedValue = Result.PreviewPanePlacement;
        AutoPlayVideoPreviewCheckBox.IsChecked = Result.AutoPlayVideoPreview;
        FontFamilyComboBox.SelectedItem = GetFontFamilyChoices().Contains(Result.FontFamily, StringComparer.OrdinalIgnoreCase)
            ? Result.FontFamily
            : AppSettings.DefaultFontFamily;
        FontSizeBox.Text = Result.FontSize.ToString(CultureInfo.InvariantCulture);
        RowHeightBox.Text = Result.RowHeight.ToString(CultureInfo.InvariantCulture);
        LoadColumnSettings();
    }

    private ThemeSelection FindThemeSelection(AppThemeMode mode, string? customName)
    {
        if (mode == AppThemeMode.Cappuccino) { mode = AppThemeMode.Custom; customName = "CappuccinoTheme.xaml"; }
        else if (mode == AppThemeMode.Cyberpunk) { mode = AppThemeMode.Custom; customName = "CyberpunkTheme.xaml"; }
        else if (mode == AppThemeMode.Black) { mode = AppThemeMode.Custom; customName = "GrayTheme.xaml"; }
        else if (mode == AppThemeMode.Gray) { mode = AppThemeMode.Custom; customName = "GrayTheme.xaml"; }

        if (ThemeComboBox.ItemsSource is IEnumerable<SettingsChoice<ThemeSelection>> choices)
        {
            foreach (var choice in choices)
            {
                if (choice.Value.Mode == mode
                    && string.Equals(choice.Value.CustomName, customName, StringComparison.OrdinalIgnoreCase))
                {
                    return choice.Value;
                }
            }
        }

        return new ThemeSelection(AppThemeMode.System);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateResult())
        {
            return;
        }

        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool TryUpdateResult()
    {
        if (ThemeComboBox.SelectedValue is ThemeSelection selection)
        {
            if (selection.Mode == AppThemeMode.Custom)
            {
                var validationError = ThemeManager.ValidateCustomTheme(selection.CustomName);
                if (validationError is not null)
                {
                    var errorMessage = validationError.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        ? _text.Format("SettingsThemeFileNotFound", selection.CustomName ?? "")
                        : _text.Format("SettingsThemeLoadFailed", selection.CustomName ?? "", validationError);
                    MessageBox.Show(
                        Window.GetWindow(this),
                        errorMessage,
                        _text.Get("SettingsThemeErrorTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }

            Result.Theme = selection.Mode;
            Result.CustomThemeName = selection.CustomName;
        }
        else
        {
            Result.Theme = AppThemeMode.System;
            Result.CustomThemeName = null;
        }

        Result.Language = LanguageComboBox.SelectedValue is AppLanguageMode language
            ? language
            : AppLanguageMode.System;
        Result.ShowHiddenFiles = ShowHiddenFilesCheckBox.IsChecked == true;
        Result.ShowSystemFiles = ShowSystemFilesCheckBox.IsChecked == true;
        Result.SortFoldersFirst = SortFoldersFirstCheckBox.IsChecked == true;
        Result.PreviewPanePlacement = PreviewPanePlacementComboBox.SelectedValue is PreviewPanePlacement previewPanePlacement
            ? previewPanePlacement
            : PreviewPanePlacement.Right;
        Result.AutoPlayVideoPreview = AutoPlayVideoPreviewCheckBox.IsChecked == true;
        var selectedFontFamily = FontFamilyComboBox.SelectedItem as string;
        Result.FontFamily = string.IsNullOrWhiteSpace(selectedFontFamily)
            ? AppSettings.DefaultFontFamily
            : selectedFontFamily.Trim();
        Result.FontSize = ParseSettingDouble(FontSizeBox.Text, AppSettings.DefaultFontSize);
        Result.RowHeight = ParseSettingDouble(RowHeightBox.Text, AppSettings.DefaultRowHeight);
        Result.VisibleColumns = GetSelectedColumnIds();
        Result.EnsureDefaults();
        return true;
    }

    private async void OpenSettingsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenFolderAsync(AppPaths.SettingsDirectory, "SettingsOpenFolderFailedTitle");
    }

    private async void OpenThemesFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenFolderAsync(AppPaths.ThemesDirectory, "SettingsOpenThemesFolderFailedTitle");
    }

    private async Task OpenFolderAsync(string path, string errorTitleKey)
    {
        try
        {
            await Task.Run(AppPaths.EnsureSettingsDirectory);
            OpenFolderRequested?.Invoke(this, new OpenFolderRequestedEventArgs(path, errorTitleKey));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                _text.Get(errorTitleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void LoadColumnSettings()
    {
        var visibleColumns = Result.VisibleColumns.Count == 0
            ? AppSettings.DefaultVisibleColumns.ToHashSet(StringComparer.Ordinal)
            : Result.VisibleColumns.ToHashSet(StringComparer.Ordinal);
        ColumnNameCheckBox.IsChecked = visibleColumns.Contains("Name");
        ColumnModifiedAtCheckBox.IsChecked = visibleColumns.Contains("ModifiedAt");
        ColumnKindCheckBox.IsChecked = visibleColumns.Contains("Kind");
        ColumnSizeCheckBox.IsChecked = visibleColumns.Contains("Size");
        ColumnExtensionCheckBox.IsChecked = visibleColumns.Contains("Extension");
        ColumnCreatedAtCheckBox.IsChecked = visibleColumns.Contains("CreatedAt");
        ColumnAccessedAtCheckBox.IsChecked = visibleColumns.Contains("AccessedAt");
        ColumnAttributesCheckBox.IsChecked = visibleColumns.Contains("Attributes");
        ColumnFullPathCheckBox.IsChecked = visibleColumns.Contains("FullPath");
        ColumnParentPathCheckBox.IsChecked = visibleColumns.Contains("ParentPath");
        ColumnBaseNameCheckBox.IsChecked = visibleColumns.Contains("BaseName");
    }

    private List<string> GetSelectedColumnIds()
    {
        var columns = new List<string>();
        AddColumnIfChecked(columns, ColumnNameCheckBox, "Name");
        AddColumnIfChecked(columns, ColumnModifiedAtCheckBox, "ModifiedAt");
        AddColumnIfChecked(columns, ColumnKindCheckBox, "Kind");
        AddColumnIfChecked(columns, ColumnSizeCheckBox, "Size");
        AddColumnIfChecked(columns, ColumnExtensionCheckBox, "Extension");
        AddColumnIfChecked(columns, ColumnCreatedAtCheckBox, "CreatedAt");
        AddColumnIfChecked(columns, ColumnAccessedAtCheckBox, "AccessedAt");
        AddColumnIfChecked(columns, ColumnAttributesCheckBox, "Attributes");
        AddColumnIfChecked(columns, ColumnFullPathCheckBox, "FullPath");
        AddColumnIfChecked(columns, ColumnParentPathCheckBox, "ParentPath");
        AddColumnIfChecked(columns, ColumnBaseNameCheckBox, "BaseName");
        return columns.Count == 0 ? [.. AppSettings.DefaultVisibleColumns] : columns;
    }

    private static void AddColumnIfChecked(List<string> columns, CheckBox checkBox, string columnId)
    {
        if (checkBox.IsChecked == true)
        {
            columns.Add(columnId);
        }
    }

    private static double ParseSettingDouble(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)
            ? parsed
            : fallback;
    }

    private static IReadOnlyList<string> GetFontFamilyChoices()
    {
        return Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Append(AppSettings.DefaultFontFamily)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private sealed record SettingsChoice<T>(string Text, T Value);
}

public sealed class OpenFolderRequestedEventArgs(string path, string errorTitleKey) : EventArgs
{
    public string Path { get; } = path;

    public string ErrorTitleKey { get; } = errorTitleKey;
}
