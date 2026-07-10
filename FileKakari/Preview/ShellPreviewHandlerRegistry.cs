using System;
using System.IO;
using Microsoft.Win32;

namespace FileKakari;

public static class ShellPreviewHandlerRegistry
{
    private const string PreviewHandlerGuid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    public static bool TryGetPreviewHandlerClsid(string filePath, out Guid clsid)
    {
        clsid = Guid.Empty;
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        // 1. HKCR\.ext\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
        if (TryGetClsidFromKey(Registry.ClassesRoot, $@"{extension}\shellex\{PreviewHandlerGuid}", out clsid))
        {
            return true;
        }

        // 2. ProgId\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
        var progId = GetProgId(extension);
        if (!string.IsNullOrEmpty(progId))
        {
            if (TryGetClsidFromKey(Registry.ClassesRoot, $@"{progId}\shellex\{PreviewHandlerGuid}", out clsid))
            {
                return true;
            }
        }

        // 3. SystemFileAssociations\.ext\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
        if (TryGetClsidFromKey(Registry.ClassesRoot, $@"SystemFileAssociations\{extension}\shellex\{PreviewHandlerGuid}", out clsid))
        {
            return true;
        }

        return false;
    }

    private static string? GetProgId(string extension)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extension);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetClsidFromKey(RegistryKey rootKey, string subKeyPath, out Guid clsid)
    {
        clsid = Guid.Empty;
        try
        {
            using var key = rootKey.OpenSubKey(subKeyPath);
            if (key is null)
            {
                return false;
            }

            var value = key.GetValue(null) as string;
            if (!string.IsNullOrEmpty(value) && Guid.TryParse(value, out clsid))
            {
                return true;
            }
        }
        catch
        {
            // Ignore registry access exceptions (e.g. security issues)
        }
        return false;
    }
}
