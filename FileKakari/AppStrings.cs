using System.Globalization;
using System.Reflection;
using System.Resources;

namespace FileKakari;

public static class AppStrings
{
    private static readonly ResourceManager ResourceManager = new("FileKakari.Resources.Strings", Assembly.GetExecutingAssembly());
    private static CultureInfo? _configuredCulture;

    public static CultureInfo EffectiveCulture
    {
        get
        {
            if (_configuredCulture is not null)
            {
                return _configuredCulture;
            }

            var culture = CultureInfo.CurrentUICulture;
            return culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) || culture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("ja-JP")
                : CultureInfo.GetCultureInfo("en-US");
        }
    }

    public static void Configure(AppLanguageMode languageMode)
    {
        _configuredCulture = languageMode switch
        {
            AppLanguageMode.Japanese => CultureInfo.GetCultureInfo("ja-JP"),
            AppLanguageMode.English => CultureInfo.GetCultureInfo("en-US"),
            _ => null
        };
    }

    public static string Get(string key)
    {
        return ResourceManager.GetString(key, EffectiveCulture)
            ?? ResourceManager.GetString(key, CultureInfo.GetCultureInfo("en-US"))
            ?? key;
    }

    public static string Format(string key, params object?[] args)
    {
        return string.Format(EffectiveCulture, Get(key), args);
    }
}
