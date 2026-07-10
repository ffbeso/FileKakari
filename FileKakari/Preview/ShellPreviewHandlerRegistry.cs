using System;
using System.IO;
using Microsoft.Win32;

namespace FileKakari;

public static class ShellPreviewHandlerRegistry
{
    private const string PreviewHandlerGuid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private static void LogDiag(string message)
    {
        PerfLog.Write($"[ShellPreviewHandlerRegistry] {message}");
    }

    public static bool TryGetPreviewHandlerClsid(string filePath, out Guid clsid)
    {
        clsid = Guid.Empty;
        if (string.IsNullOrEmpty(filePath))
        {
            LogDiag("TryGetPreviewHandlerClsid failed: File path is empty or null.");
            return false;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            LogDiag($"TryGetPreviewHandlerClsid failed: Extension is empty or null for path: '{filePath}'");
            return false;
        }

        LogDiag($"TryGetPreviewHandlerClsid starting lookup for extension '{extension}' (file: '{filePath}')");

        // Scan Registry64 and Registry32 explicitly.
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        // Query multiple potential base locations:
        // 1. ClassesRoot (merged HKCU and HKLM classes)
        // 2. CurrentUser\Software\Classes
        // 3. LocalMachine\Software\Classes
        var locations = new[]
        {
            (Hive: RegistryHive.ClassesRoot, SubPath: ""),
            (Hive: RegistryHive.CurrentUser, SubPath: @"Software\Classes"),
            (Hive: RegistryHive.LocalMachine, SubPath: @"Software\Classes")
        };

        foreach (var view in views)
        {
            foreach (var loc in locations)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, view);
                    var prefixPath = string.IsNullOrEmpty(loc.SubPath) ? "" : loc.SubPath + @"\";

                    // Rule 1: {prefix}\.ext\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
                    var directPath = prefixPath + $@"{extension}\shellex\{PreviewHandlerGuid}";
                    if (TryGetClsidFromKeyWithDiag(baseKey, directPath, loc.Hive, view, out clsid))
                    {
                        LogDiag($"Success: Found CLSID {clsid} using direct association path: '{loc.Hive}\\{directPath}' ({view} view)");
                        return true;
                    }

                    // Rule 2: {prefix}\<ProgId>\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
                    var progId = GetProgIdFromBase(baseKey, prefixPath + extension, loc.Hive, view);
                    if (!string.IsNullOrEmpty(progId))
                    {
                        var progIdPath = prefixPath + $@"{progId}\shellex\{PreviewHandlerGuid}";
                        if (TryGetClsidFromKeyWithDiag(baseKey, progIdPath, loc.Hive, view, out clsid))
                        {
                            LogDiag($"Success: Found CLSID {clsid} using ProgId '{progId}' path: '{loc.Hive}\\{progIdPath}' ({view} view)");
                            return true;
                        }
                    }

                    // Rule 3: {prefix}\SystemFileAssociations\.ext\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
                    var systemPath = prefixPath + $@"SystemFileAssociations\{extension}\shellex\{PreviewHandlerGuid}";
                    if (TryGetClsidFromKeyWithDiag(baseKey, systemPath, loc.Hive, view, out clsid))
                    {
                        LogDiag($"Success: Found CLSID {clsid} using SystemFileAssociations path: '{loc.Hive}\\{systemPath}' ({view} view)");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogDiag($"Hive query exception for base Hive '{loc.Hive}' SubPath '{loc.SubPath}' ({view}): {ex.Message}");
                }
            }
        }

        LogDiag($"TryGetPreviewHandlerClsid finished: No Preview Handler found for extension '{extension}'");
        return false;
    }

    private static string? GetProgIdFromBase(RegistryKey baseKey, string extensionPath, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var key = baseKey.OpenSubKey(extensionPath);
            if (key is null)
            {
                return null;
            }
            var val = key.GetValue(null) as string;
            LogDiag($"ProgId query: Checked '{hive}\\{extensionPath}' ({view}) -> found value '{val ?? "null"}'");
            return val;
        }
        catch (Exception ex)
        {
            LogDiag($"ProgId query exception: Checked '{hive}\\{extensionPath}' ({view}) -> {ex.Message}");
            return null;
        }
    }

    private static bool TryGetClsidFromKeyWithDiag(RegistryKey baseKey, string subKeyPath, RegistryHive hive, RegistryView view, out Guid clsid)
    {
        clsid = Guid.Empty;
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath);
            if (key is null)
            {
                LogDiag($"Check: Key '{hive}\\{subKeyPath}' ({view}) does not exist.");
                return false;
            }

            var value = key.GetValue(null) as string;
            if (string.IsNullOrEmpty(value))
            {
                LogDiag($"Check: Key '{hive}\\{subKeyPath}' ({view}) exists but has no default value.");
                return false;
            }

            if (Guid.TryParse(value, out clsid))
            {
                LogDiag($"Check: Key '{hive}\\{subKeyPath}' ({view}) has CLSID '{value}' (Parsed successfully).");
                return true;
            }

            LogDiag($"Check: Key '{hive}\\{subKeyPath}' ({view}) has value '{value}' but it failed to parse as GUID.");
        }
        catch (Exception ex)
        {
            LogDiag($"Check exception: Key '{hive}\\{subKeyPath}' ({view}) -> {ex.Message}");
        }
        return false;
    }
}
