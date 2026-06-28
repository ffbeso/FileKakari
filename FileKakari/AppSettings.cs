namespace FileKakari;

public sealed class AppSettings
{
    public static readonly string[] DefaultVisibleColumns = ["Name", "Kind", "Size", "ModifiedAt"];
    public const string DefaultFontFamily = "Segoe UI";
    public const double DefaultFontSize = 12;
    public const double DefaultRowHeight = 24;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 32;
    public const double MinRowHeight = 18;
    public const double MaxRowHeight = 64;
    private static readonly Dictionary<string, double> DefaultColumnWidths = new()
    {
        ["Name"] = 400,
        ["Kind"] = 100,
        ["Size"] = 75,
        ["ModifiedAt"] = 130,
        ["CreatedAt"] = 130,
        ["AccessedAt"] = 130,
        ["Extension"] = 60,
        ["Attributes"] = 70,
        ["FullPath"] = 240,
        ["ParentPath"] = 200,
        ["BaseName"] = 150
    };

    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    public string? CustomThemeName { get; set; }

    public AppLanguageMode Language { get; set; } = AppLanguageMode.System;

    public bool ShowHiddenFiles { get; set; }

    public bool ShowSystemFiles { get; set; }

    public List<string> VisibleColumns { get; set; } = [.. DefaultVisibleColumns];

    public Dictionary<string, double> ColumnWidths { get; set; } = new(DefaultColumnWidths);

    public Dictionary<string, FolderColumnWidthsState> FolderColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SortFoldersFirst { get; set; } = true;

    public bool AutoPlayVideoPreview { get; set; }

    public PreviewPanePlacement PreviewPanePlacement { get; set; } = PreviewPanePlacement.Right;

    public FileDisplayMode DisplayMode { get; set; } = FileDisplayMode.Details;

    public string FontFamily { get; set; } = DefaultFontFamily;

    public double FontSize { get; set; } = DefaultFontSize;

    public double RowHeight { get; set; } = DefaultRowHeight;

    public AppSettings Clone()
    {
        EnsureDefaults();
        return new AppSettings
        {
            Theme = Theme,
            CustomThemeName = CustomThemeName,
            Language = Language,
            ShowHiddenFiles = ShowHiddenFiles,
            ShowSystemFiles = ShowSystemFiles,
            VisibleColumns = [.. VisibleColumns],
            ColumnWidths = new Dictionary<string, double>(ColumnWidths),
            FolderColumnWidths = FolderColumnWidths.ToDictionary(
                kvp => kvp.Key,
                kvp => new FolderColumnWidthsState
                {
                    LastAccessUtc = kvp.Value.LastAccessUtc,
                    Widths = new Dictionary<string, double>(kvp.Value.Widths)
                },
                StringComparer.OrdinalIgnoreCase),
            SortFoldersFirst = SortFoldersFirst,
            AutoPlayVideoPreview = AutoPlayVideoPreview,
            PreviewPanePlacement = PreviewPanePlacement,
            DisplayMode = DisplayMode,
            FontFamily = FontFamily,
            FontSize = FontSize,
            RowHeight = RowHeight
        };
    }

    public void EnsureDefaults()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily)
            ? DefaultFontFamily
            : FontFamily.Trim();
        FontSize = IsFiniteInRange(FontSize, MinFontSize, MaxFontSize) ? FontSize : DefaultFontSize;
        RowHeight = IsFiniteInRange(RowHeight, MinRowHeight, MaxRowHeight) ? RowHeight : DefaultRowHeight;
        DisplayMode = NormalizeDisplayMode(DisplayMode);
        PreviewPanePlacement = NormalizePreviewPanePlacement(PreviewPanePlacement);

        // Migrate visible columns
        if (VisibleColumns is not null)
        {
            for (int i = 0; i < VisibleColumns.Count; i++)
            {
                VisibleColumns[i] = ColumnLayoutService.NormalizeColumnId(VisibleColumns[i]);
            }

            // Deduplicate columns (prevent having both old and new keys duplicated)
            var unique = VisibleColumns.Distinct(StringComparer.Ordinal).ToList();
            if (unique.Count != VisibleColumns.Count)
            {
                VisibleColumns = unique;
            }
        }

        if (VisibleColumns is null || VisibleColumns.Count == 0)
        {
            VisibleColumns = [.. DefaultVisibleColumns];
        }

        ColumnWidths ??= [];

        // Migrate column widths keys
        var oldKeys = ColumnWidths.Keys.ToList();
        foreach (var key in oldKeys)
        {
            var normalized = ColumnLayoutService.NormalizeColumnId(key);
            if (normalized != key)
            {
                if (ColumnWidths.Remove(key, out var width))
                {
                    ColumnWidths.TryAdd(normalized, width);
                }
            }
        }

        foreach (var (columnId, width) in DefaultColumnWidths)
        {
            ColumnWidths.TryAdd(columnId, width);
        }

        FolderColumnWidths ??= new(StringComparer.OrdinalIgnoreCase);

        // Migrate folder column widths keys
        if (FolderColumnWidths is not null)
        {
            foreach (var state in FolderColumnWidths.Values)
            {
                if (state.Widths is not null)
                {
                    var oldFolderKeys = state.Widths.Keys.ToList();
                    foreach (var key in oldFolderKeys)
                    {
                        var normalized = ColumnLayoutService.NormalizeColumnId(key);
                        if (normalized != key)
                        {
                            if (state.Widths.Remove(key, out var w))
                            {
                                state.Widths.TryAdd(normalized, w);
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool IsFiniteInRange(double value, double min, double max)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max;
    }

    public static FileDisplayMode NormalizeDisplayMode(FileDisplayMode mode)
    {
        return Enum.IsDefined(mode) && mode != FileDisplayMode.Tiles
            ? mode
            : FileDisplayMode.List;
    }

    public static PreviewPanePlacement NormalizePreviewPanePlacement(PreviewPanePlacement placement)
    {
        return Enum.IsDefined(placement)
            ? placement
            : PreviewPanePlacement.Right;
    }
}

public enum AppThemeMode
{
    System,
    Light,
    Dark,
    Cappuccino,
    Cyberpunk,
    Black,
    Gray,
    Custom
}

public enum AppLanguageMode
{
    System,
    Japanese,
    English
}

public enum FileDisplayMode
{
    Details,
    Compact,
    List,
    Tiles
}

public enum PreviewPanePlacement
{
    Right,
    Bottom
}

public sealed class FolderColumnWidthsState
{
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, double> Widths { get; set; } = [];
}
