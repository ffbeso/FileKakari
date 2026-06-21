namespace FileKakari;

public sealed class LocalizationService
{
    public string Get(string key)
    {
        return AppStrings.Get(key);
    }

    public string Format(string key, params object?[] args)
    {
        return AppStrings.Format(key, args);
    }
}
