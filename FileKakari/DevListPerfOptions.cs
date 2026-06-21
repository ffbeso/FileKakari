using System.Windows.Controls;

namespace FileKakari;

public sealed class DevListPerfOptions
{
    private const string LightListVariable = "FILEKAKARI_DEV_LIGHT_LIST";
    private const string ShellIconsVariable = "FILEKAKARI_DEV_SHELL_ICONS";
    private const string GridLinesVariable = "FILEKAKARI_DEV_GRID_LINES";
    private const string HoverVariable = "FILEKAKARI_DEV_HOVER";
    private const string MinimalSelectionVariable = "FILEKAKARI_DEV_MINIMAL_SELECTION";
    private const string DiagnosticRowStyleVariable = "FILEKAKARI_DEV_DIAGNOSTIC_ROW_STYLE";
    private const string CanContentScrollVariable = "FILEKAKARI_DEV_CAN_CONTENT_SCROLL";
    private const string ScrollUnitVariable = "FILEKAKARI_DEV_SCROLL_UNIT";
    private const string ScrollOnlyVariable = "FILEKAKARI_DEV_SCROLL_ONLY";
    private const string PanningModeVariable = "FILEKAKARI_DEV_PANNING_MODE";
    private const string PreviewMouseWheelVariable = "FILEKAKARI_DEV_PREVIEW_MOUSE_WHEEL";
    private const string ScrollTraceVariable = "FILEKAKARI_DEV_SCROLL_TRACE";
    private const string MouseWheelLinesVariable = "FILEKAKARI_DEV_MOUSE_WHEEL_LINES";
    private const string MouseWheelPixelsVariable = "FILEKAKARI_DEV_MOUSE_WHEEL_PIXELS";
    private const string SortVariable = "FILEKAKARI_DEV_SORT";
    private const string StatusAggregationVariable = "FILEKAKARI_DEV_STATUS_AGGREGATION";
    private const string ExtraColumnsVariable = "FILEKAKARI_DEV_EXTRA_COLUMNS";
    private const string SessionRestoreVariable = "FILEKAKARI_DEV_SESSION_RESTORE";

    public bool Enabled { get; private set; }

    public bool ShellIconsEnabled { get; private set; } = true;

    public bool GridLinesEnabled { get; private set; } = true;

    public bool HoverEnabled { get; private set; } = true;

    public bool MinimalSelection { get; private set; }

    public bool DiagnosticRowStyleEnabled { get; private set; }

    public bool CanContentScroll { get; private set; } = true;

    public ScrollUnit ScrollUnit { get; private set; } = ScrollUnit.Pixel;

    public PanningMode PanningMode { get; private set; } = PanningMode.None;

    public bool PreviewMouseWheelEnabled { get; private set; }

    public bool ScrollTraceEnabled { get; private set; }

    public bool SortEnabled { get; private set; } = true;

    public bool StatusAggregationEnabled { get; private set; } = true;

    public bool ExtraColumnsEnabled { get; private set; } = true;

    public bool SessionRestoreEnabled { get; private set; } = true;

    public int MouseWheelLines { get; private set; } = 3;

    public double MouseWheelPixels { get; private set; } = 96;

    public static DevListPerfOptions FromEnvironment()
    {
        var options = new DevListPerfOptions();
        var scrollOnly = ReadBoolean(ScrollOnlyVariable);
        if (scrollOnly is not null)
        {
            options.Enabled = true;
        }

        var lightList = ReadBoolean(LightListVariable);
        if (lightList == true)
        {
            options.Enabled = true;
            options.ShellIconsEnabled = false;
            options.GridLinesEnabled = false;
            options.HoverEnabled = false;
            options.MinimalSelection = true;
            options.DiagnosticRowStyleEnabled = true;
        }
        else if (lightList == false)
        {
            options.Enabled = true;
        }

        options.ApplyBoolean(ShellIconsVariable, value => options.ShellIconsEnabled = value);
        options.ApplyBoolean(GridLinesVariable, value => options.GridLinesEnabled = value);
        options.ApplyBoolean(HoverVariable, value => options.HoverEnabled = value);
        options.ApplyBoolean(MinimalSelectionVariable, value => options.MinimalSelection = value);
        options.ApplyBoolean(DiagnosticRowStyleVariable, value => options.DiagnosticRowStyleEnabled = value);
        options.ApplyBoolean(CanContentScrollVariable, value => options.CanContentScroll = value);
        options.ApplyBoolean(PreviewMouseWheelVariable, value => options.PreviewMouseWheelEnabled = value);
        options.ApplyBoolean(ScrollTraceVariable, value => options.ScrollTraceEnabled = value);
        options.ApplyBoolean(SortVariable, value => options.SortEnabled = value);
        options.ApplyBoolean(StatusAggregationVariable, value => options.StatusAggregationEnabled = value);
        options.ApplyBoolean(ExtraColumnsVariable, value => options.ExtraColumnsEnabled = value);
        options.ApplyBoolean(SessionRestoreVariable, value => options.SessionRestoreEnabled = value);

        if (TryReadScrollUnit(out var scrollUnit))
        {
            options.Enabled = true;
            options.ScrollUnit = scrollUnit;
        }

        if (TryReadPanningMode(out var panningMode))
        {
            options.Enabled = true;
            options.PanningMode = panningMode;
        }

        if (TryReadInt(MouseWheelLinesVariable, out var mouseWheelLines))
        {
            options.Enabled = true;
            options.MouseWheelLines = Math.Max(1, mouseWheelLines);
            options.PreviewMouseWheelEnabled = true;
        }

        if (TryReadDouble(MouseWheelPixelsVariable, out var mouseWheelPixels))
        {
            options.Enabled = true;
            options.MouseWheelPixels = Math.Max(1, mouseWheelPixels);
            options.PreviewMouseWheelEnabled = true;
        }

        if (!options.GridLinesEnabled || !options.HoverEnabled || options.MinimalSelection)
        {
            options.DiagnosticRowStyleEnabled = true;
        }

        return options;
    }

    public string Describe()
    {
        return $"enabled={Enabled} shellIcons={ShellIconsEnabled} gridLines={GridLinesEnabled} hover={HoverEnabled} minimalSelection={MinimalSelection} diagnosticRowStyle={DiagnosticRowStyleEnabled} canContentScroll={CanContentScroll} scrollUnit={ScrollUnit} panningMode={PanningMode} previewMouseWheel={PreviewMouseWheelEnabled} scrollTrace={ScrollTraceEnabled} sort={SortEnabled} statusAggregation={StatusAggregationEnabled} extraColumns={ExtraColumnsEnabled} sessionRestore={SessionRestoreEnabled} mouseWheelLines={MouseWheelLines} mouseWheelPixels={MouseWheelPixels}";
    }

    private void ApplyBoolean(string variableName, Action<bool> apply)
    {
        var value = ReadBoolean(variableName);
        if (value is null)
        {
            return;
        }

        Enabled = true;
        apply(value.Value);
    }

    private static bool? ReadBoolean(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }

    private static bool TryReadScrollUnit(out ScrollUnit scrollUnit)
    {
        var value = Environment.GetEnvironmentVariable(ScrollUnitVariable);
        scrollUnit = ScrollUnit.Pixel;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "item" or "items" => SetScrollUnit(ScrollUnit.Item, out scrollUnit),
            "pixel" or "pixels" => SetScrollUnit(ScrollUnit.Pixel, out scrollUnit),
            _ => false
        };
    }

    private static bool SetScrollUnit(ScrollUnit value, out ScrollUnit scrollUnit)
    {
        scrollUnit = value;
        return true;
    }

    private static bool TryReadPanningMode(out PanningMode panningMode)
    {
        var value = Environment.GetEnvironmentVariable(PanningModeVariable);
        panningMode = PanningMode.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => SetPanningMode(PanningMode.None, out panningMode),
            "both" => SetPanningMode(PanningMode.Both, out panningMode),
            "horizontalonly" or "horizontal" => SetPanningMode(PanningMode.HorizontalOnly, out panningMode),
            "verticalonly" or "vertical" => SetPanningMode(PanningMode.VerticalOnly, out panningMode),
            "horizontalfirst" => SetPanningMode(PanningMode.HorizontalFirst, out panningMode),
            "verticalfirst" => SetPanningMode(PanningMode.VerticalFirst, out panningMode),
            _ => false
        };
    }

    private static bool SetPanningMode(PanningMode value, out PanningMode panningMode)
    {
        panningMode = value;
        return true;
    }

    private static bool TryReadInt(string variableName, out int result)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return int.TryParse(value, out result);
    }

    private static bool TryReadDouble(string variableName, out double result)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return double.TryParse(value, out result);
    }
}
