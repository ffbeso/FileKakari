using System.IO;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileKakari;

public static class ThemeManager
{
    private const string ThemeSentinelKey = "__FileKakariThemeMarker";
    private static readonly string[] CurrentThemeResourceKeys =
    [
        "ActivePaneBorderBrush",
        "InactivePaneBorderBrush",
        "ActivePaneAccentBrush",
        "InactivePaneAccentBrush",
        "TabSelectedBackgroundBrush",
        "TabHoverBackgroundBrush",
        "PanelBackgroundBrush",
        "BorderBrush",
        "SubtleTextBrush"
    ];
    public const string ThemeResourcesFileName = "ThemeResources.xaml";
    private static bool _initialized = false;
    private static readonly object InitGate = new();

    public static void InitializeUserThemes()
    {
        lock (InitGate)
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
        }

        try
        {
            AppPaths.EnsureSettingsDirectory();
            var userThemesDir = AppPaths.ThemesDirectory;
            if (!Directory.Exists(userThemesDir))
            {
                Directory.CreateDirectory(userThemesDir);
            }

            var resourcesPath = Path.Combine(userThemesDir, ThemeResourcesFileName);
            if (File.Exists(resourcesPath))
            {
                File.Delete(resourcesPath);
                PerfLog.Write($"theme-template-removed file=\"{ThemeResourcesFileName}\"");
            }

            var obsoleteManagedThemes = new[]
            {
                "LightTheme.xaml",
                "DarkTheme.xaml",
                "BlackTheme.xaml"
            };

            var baseThemesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
            foreach (var themeFile in obsoleteManagedThemes)
            {
                var obsoletePath = Path.Combine(userThemesDir, themeFile);
                if (File.Exists(obsoletePath))
                {
                    File.Delete(obsoletePath);
                    PerfLog.Write($"theme-template-removed file=\"{themeFile}\"");
                }
            }

            if (!Directory.Exists(baseThemesDir))
            {
                return;
            }

            foreach (var srcPath in Directory.EnumerateFiles(baseThemesDir, "*.xaml"))
            {
                var themeFile = Path.GetFileName(srcPath);
                if (IsReservedThemeFile(themeFile))
                {
                    continue;
                }

                var destPath = Path.Combine(userThemesDir, themeFile);
                if (!File.Exists(destPath))
                {
                    File.Copy(srcPath, destPath, overwrite: false);
                    PerfLog.Write($"theme-template-copied file=\"{themeFile}\"");
                    continue;
                }

                var missingKeys = GetMissingThemeResourceKeys(destPath);
                if (missingKeys.Count > 0)
                {
                    PerfLog.Write($"theme-template-outdated file=\"{themeFile}\" missingKeys=\"{string.Join(',', missingKeys)}\" action=preserved");
                }
            }
        }
        catch (Exception ex)
        {
            PerfLog.Write($"theme-template-init-failed error=\"{ex.Message}\"");
        }
    }

    public static IReadOnlyList<ThemeDefinition> DiscoverCustomThemes()
    {
        InitializeUserThemes();

        try
        {
            if (!Directory.Exists(AppPaths.ThemesDirectory))
            {
                return [];
            }

            var themes = new List<ThemeDefinition>();
            foreach (var filePath in Directory.EnumerateFiles(AppPaths.ThemesDirectory, "*.xaml"))
            {
                var fileName = Path.GetFileName(filePath);
                if (IsReservedThemeFile(fileName))
                {
                    continue;
                }

                var loadError = TryLoadThemeDictionary(filePath, out _);
                if (loadError is not null)
                {
                    PerfLog.Write($"theme-discovery-skipped file=\"{fileName}\" reason=\"{loadError}\"");
                    continue;
                }

                var missingKeys = GetMissingThemeResourceKeys(filePath);
                if (missingKeys.Count > 0)
                {
                    PerfLog.Write($"theme-discovery-fallback file=\"{fileName}\" missingKeys=\"{string.Join(',', missingKeys)}\"");
                }

                themes.Add(new ThemeDefinition(fileName, GetThemeDisplayName(fileName)));
            }

            return themes
                .OrderBy(theme => theme.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(theme => theme.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            PerfLog.Write($"theme-discovery-failed error=\"{ex.Message}\"");
            return [];
        }
    }

    private static string GetThemeDisplayName(string fileName)
    {
        var displayName = Path.GetFileNameWithoutExtension(fileName);
        return displayName.EndsWith("Theme", StringComparison.OrdinalIgnoreCase)
            ? displayName[..^"Theme".Length]
            : displayName;
    }

    private static bool IsReservedThemeFile(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            || string.Equals(fileName, ThemeResourcesFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "DarkTheme.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetMissingThemeResourceKeys(string themePath)
    {
        if (!File.Exists(themePath))
        {
            return CurrentThemeResourceKeys;
        }

        try
        {
            using var stream = File.OpenRead(themePath);
            if (XamlReader.Load(stream) is not ResourceDictionary dictionary)
            {
                return CurrentThemeResourceKeys;
            }

            return CurrentThemeResourceKeys
                .Where(key => !dictionary.Contains(key))
                .ToList();
        }
        catch
        {
            return CurrentThemeResourceKeys;
        }
    }

    public static string? ValidateCustomTheme(string? customThemeName)
    {
        InitializeUserThemes();

        if (string.IsNullOrWhiteSpace(customThemeName))
        {
            return "Theme name is empty.";
        }

        // Path validation constraints:
        // - Absolute path is forbidden
        // - `..` containing relative path is forbidden
        // - Subdirectory path separators (/ or \) are forbidden
        // - Extension must be .xaml
        // - Must match its own filename exactly (Path.GetFileName(customThemeName) == customThemeName)
        var isValidName = !string.IsNullOrEmpty(customThemeName)
            && !string.Equals(customThemeName, ThemeResourcesFileName, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(customThemeName) == customThemeName
            && string.Equals(Path.GetExtension(customThemeName), ".xaml", StringComparison.OrdinalIgnoreCase)
            && !customThemeName.Contains("..")
            && !customThemeName.Contains('/')
            && !customThemeName.Contains('\\')
            && !Path.IsPathRooted(customThemeName)
            && customThemeName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        if (!isValidName)
        {
            return "Invalid theme file name. ThemeResources.xaml, absolute paths, directory paths, '..', and non-.xaml extensions are not allowed.";
        }

        var filePath = Path.Combine(AppPaths.ThemesDirectory, customThemeName);
        if (!File.Exists(filePath))
        {
            return $"Theme file '{customThemeName}' not found in Themes folder.";
        }

        return TryLoadThemeDictionary(filePath, out _);
    }

    public static string? Apply(Window window, AppThemeMode themeMode, string? customThemeName = null)
    {
        var result = Apply(themeMode, customThemeName);
        if (result == null && window != null)
        {
            UpdateWindowTitleBar(window);
        }
        return result;
    }

    public static string? Apply(AppThemeMode themeMode, string? customThemeName = null)
    {
        InitializeUserThemes();

        // Migrate legacy theme modes to Custom theme files
        if (themeMode == AppThemeMode.Cappuccino) { themeMode = AppThemeMode.Custom; customThemeName = "CappuccinoTheme.xaml"; }
        else if (themeMode == AppThemeMode.Cyberpunk) { themeMode = AppThemeMode.Custom; customThemeName = "CyberpunkTheme.xaml"; }
        else if (themeMode == AppThemeMode.Black) { themeMode = AppThemeMode.Custom; customThemeName = "GrayTheme.xaml"; }
        else if (themeMode == AppThemeMode.Gray) { themeMode = AppThemeMode.Custom; customThemeName = "GrayTheme.xaml"; }

        if (themeMode == AppThemeMode.Custom)
        {
            var validationError = ValidateCustomTheme(customThemeName);
            if (validationError != null)
            {
                PerfLog.Write($"theme-apply-failed mode=custom file=\"{customThemeName}\" reason=\"{validationError}\"");
                return validationError;
            }

            var filePath = Path.Combine(AppPaths.ThemesDirectory, customThemeName!);
            try
            {
                var loadError = TryLoadThemeDictionary(filePath, out var customDict);
                if (loadError is not null || customDict is null)
                {
                    throw new InvalidOperationException(loadError ?? "The theme dictionary could not be loaded.");
                }

                customDict[ThemeSentinelKey] = true;
                var fallbackDict = new ResourceDictionary
                {
                    Source = new Uri(ResolveSystemThemeDictionary(), UriKind.Relative)
                };

                var dictionaries = Application.Current.Resources.MergedDictionaries;
                dictionaries.Add(fallbackDict);
                dictionaries.Add(customDict);
                for (var i = dictionaries.Count - 3; i >= 0; i--)
                {
                    var dict = dictionaries[i];
                    if (dict.Contains(ThemeSentinelKey) || IsBuiltInThemeDictionary(dict.Source?.OriginalString))
                    {
                        dictionaries.RemoveAt(i);
                    }
                }
                PerfLog.Write($"theme-applied mode=custom file=\"{customThemeName}\"");
                return null;
            }
            catch (Exception ex)
            {
                var errMsg = $"Failed to load theme '{customThemeName}': {ex.Message}";
                PerfLog.Write($"theme-apply-failed mode=custom file=\"{customThemeName}\" error=\"{ex.Message}\"");
                return errMsg;
            }
        }

        // Apply built-in theme
        var themeDictionary = ResolveThemeDictionary(themeMode);
        ApplyBuiltInTheme(themeDictionary);
        PerfLog.Write($"theme-applied mode={themeMode} dictionary=\"{themeDictionary}\"");
        return null;
    }

    private static void ApplyBuiltInTheme(string themeDictionary)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var dict = dictionaries[i];
            if (dict.Contains(ThemeSentinelKey) || IsBuiltInThemeDictionary(dict.Source?.OriginalString))
            {
                dictionaries.RemoveAt(i);
            }
        }
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(themeDictionary, UriKind.Relative)
        });
    }

    private static string ResolveThemeDictionary(AppThemeMode themeMode)
    {
        return themeMode switch
        {
            AppThemeMode.Light => "Themes/LightTheme.xaml",
            AppThemeMode.Dark => "Themes/DarkTheme.xaml",
            _ => ResolveSystemThemeDictionary()
        };
    }

    private static string ResolveSystemThemeDictionary()
    {
        var forcedTheme = Environment.GetEnvironmentVariable("FILEKAKARI_THEME");
        if (TryResolveThemeName(forcedTheme, out var forcedThemeDictionary))
        {
            return forcedThemeDictionary;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? "Themes/DarkTheme.xaml"
                : "Themes/LightTheme.xaml";
        }
        catch
        {
            return "Themes/LightTheme.xaml";
        }
    }

    private static bool TryResolveThemeName(string? name, out string themeDictionary)
    {
        themeDictionary = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        themeDictionary = name.Trim().ToLowerInvariant() switch
        {
            "light" => "Themes/LightTheme.xaml",
            "dark" => "Themes/DarkTheme.xaml",
            _ => ""
        };
        return themeDictionary.Length > 0;
    }

    private static bool IsBuiltInThemeDictionary(string? source)
    {
        return source is "Themes/LightTheme.xaml"
            or "Themes/DarkTheme.xaml";
    }

    private static string? TryLoadThemeDictionary(string filePath, out ResourceDictionary? dictionary)
    {
        dictionary = null;
        try
        {
            using var stream = File.OpenRead(filePath);
            if (XamlReader.Load(stream) is not ResourceDictionary loadedDictionary)
            {
                return "The loaded XAML root element is not a ResourceDictionary.";
            }

            dictionary = loadedDictionary;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    public static void UpdateWindowTitleBar(Window window)
    {
        if (window == null) return;
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            Color? resolvedCaptionColor = GetThemeColor("TitleBarBackgroundColor")
                ?? GetThemeColor("HeaderBackgroundColor")
                ?? GetThemeColor("AppBackgroundColor");

            bool isDark = false;
            if (resolvedCaptionColor == null)
            {
                var txtColor = GetThemeColor("TitleBarTextColor") ?? GetThemeColor("TextColor");
                if (txtColor != null)
                {
                    isDark = (txtColor.Value.R + txtColor.Value.G + txtColor.Value.B > 380);
                }
            }
            else
            {
                isDark = (resolvedCaptionColor.Value.R + resolvedCaptionColor.Value.G + resolvedCaptionColor.Value.B < 380);
            }

            Color captionColor = resolvedCaptionColor ?? (isDark ? Color.FromRgb(32, 33, 36) : Color.FromRgb(247, 247, 247));
            Color textColor = GetThemeColor("TitleBarTextColor")
                ?? GetThemeColor("TextColor")
                ?? (isDark ? Colors.White : Colors.Black);

            int darkAttribute = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkAttribute, sizeof(int));

            int captionColorRef = captionColor.R | (captionColor.G << 8) | (captionColor.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));

            int textColorRef = textColor.R | (textColor.G << 8) | (textColor.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColorRef, sizeof(int));
        }
        catch (Exception ex)
        {
            PerfLog.Write($"dwm-titlebar-update-failed error=\"{ex.Message}\"");
        }
    }

    private static Color? GetThemeColor(string resourceKey)
    {
        if (Application.Current != null && Application.Current.Resources.Contains(resourceKey))
        {
            var value = Application.Current.Resources[resourceKey];
            if (value is Color color)
            {
                return color;
            }
            if (value is SolidColorBrush brush)
            {
                return brush.Color;
            }
        }
        return null;
    }
}

public sealed record ThemeSelection(AppThemeMode Mode, string? CustomName = null);

public sealed record ThemeDefinition(string FileName, string DisplayName);
