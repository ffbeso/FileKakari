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
        if (TryGetPreviewHandler(filePath, out var registration))
        {
            clsid = registration.Clsid;
            return true;
        }

        clsid = Guid.Empty;
        return false;
    }

    public static bool TryGetPreviewHandler(string filePath, out ShellPreviewHandlerRegistration registration)
    {
        registration = ShellPreviewHandlerRegistration.Empty;
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
                    if (TryGetClsidFromKeyWithDiag(baseKey, directPath, loc.Hive, view, out var clsid))
                    {
                        registration = CreateRegistration(clsid, extension, null, null, null, loc.Hive, view, directPath, "extension-direct");
                        LogDiag($"Success: Found CLSID {clsid} using direct association path: '{loc.Hive}\\{directPath}' ({view} view)");
                        return true;
                    }

                    // Rule 2: {prefix}\<ProgId>\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
                    var progId = GetEffectiveProgIdFromBase(baseKey, prefixPath, extension, loc.Hive, view)
                        ?? GetProgIdFromBase(baseKey, prefixPath + extension, loc.Hive, view);
                    var perceivedType = GetPerceivedTypeFromBase(baseKey, prefixPath, extension, progId, loc.Hive, view);
                    var contentType = GetContentTypeFromBase(baseKey, prefixPath, extension, progId, loc.Hive, view);
                    if (!string.IsNullOrEmpty(progId))
                    {
                        var progIdPath = prefixPath + $@"{progId}\shellex\{PreviewHandlerGuid}";
                        if (TryGetClsidFromKeyWithDiag(baseKey, progIdPath, loc.Hive, view, out clsid))
                        {
                            registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, progIdPath, "progid");
                            LogDiag($"Success: Found CLSID {clsid} using ProgId '{progId}' path: '{loc.Hive}\\{progIdPath}' ({view} view)");
                            return true;
                        }
                    }

                    // Rule 3: {prefix}\SystemFileAssociations\.ext\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
                    var systemPath = prefixPath + $@"SystemFileAssociations\{extension}\shellex\{PreviewHandlerGuid}";
                    if (TryGetClsidFromKeyWithDiag(baseKey, systemPath, loc.Hive, view, out clsid))
                    {
                        registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, systemPath, "system-extension");
                        LogDiag($"Success: Found CLSID {clsid} using SystemFileAssociations path: '{loc.Hive}\\{systemPath}' ({view} view)");
                        return true;
                    }

                    if (!string.IsNullOrEmpty(perceivedType))
                    {
                        var perceivedPath = prefixPath + $@"SystemFileAssociations\{perceivedType}\shellex\{PreviewHandlerGuid}";
                        if (TryGetClsidFromKeyWithDiag(baseKey, perceivedPath, loc.Hive, view, out clsid))
                        {
                            registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, perceivedPath, "system-perceived-type");
                            LogDiag($"Success: Found CLSID {clsid} using PerceivedType '{perceivedType}' path: '{loc.Hive}\\{perceivedPath}' ({view} view)");
                            return true;
                        }
                    }

                    var textPath = prefixPath + $@"SystemFileAssociations\text\shellex\{PreviewHandlerGuid}";
                    if (IsTextAssociation(perceivedType, contentType)
                        && TryGetClsidFromKeyWithDiag(baseKey, textPath, loc.Hive, view, out clsid))
                    {
                        registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, textPath, "system-text");
                        LogDiag($"Success: Found CLSID {clsid} using SystemFileAssociations text path: '{loc.Hive}\\{textPath}' ({view} view)");
                        return true;
                    }

                    var wildcardPath = prefixPath + $@"*\shellex\{PreviewHandlerGuid}";
                    if (TryGetClsidFromKeyWithDiag(baseKey, wildcardPath, loc.Hive, view, out clsid))
                    {
                        registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, wildcardPath, "wildcard");
                        LogDiag($"Success: Found CLSID {clsid} using wildcard path: '{loc.Hive}\\{wildcardPath}' ({view} view)");
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

    private static ShellPreviewHandlerRegistration CreateRegistration(
        Guid clsid,
        string extension,
        string? progId,
        string? perceivedType,
        string? contentType,
        RegistryHive hive,
        RegistryView view,
        string registryPath,
        string sourceKind)
    {
        var description = GetClsidDescription(clsid, view);
        LogDiag($"CLSID registration: clsid={clsid:B} description=\"{description ?? ""}\" sourceKind=\"{sourceKind}\" path=\"{hive}\\{registryPath}\" view={view} progId=\"{progId ?? ""}\" perceivedType=\"{perceivedType ?? ""}\" contentType=\"{contentType ?? ""}\"");
        return new ShellPreviewHandlerRegistration(
            clsid,
            extension,
            progId,
            perceivedType,
            contentType,
            hive,
            view,
            registryPath,
            sourceKind,
            description);
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

    private static string? GetEffectiveProgIdFromBase(RegistryKey baseKey, string prefixPath, string extension, RegistryHive hive, RegistryView view)
    {
        var userChoicePath = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice";
        if (hive != RegistryHive.CurrentUser)
        {
            return null;
        }

        try
        {
            using var key = baseKey.OpenSubKey(userChoicePath);
            var value = key?.GetValue("ProgId") as string;
            LogDiag($"UserChoice ProgId query: Checked '{hive}\\{userChoicePath}' ({view}) -> found value '{value ?? "null"}'");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            LogDiag($"UserChoice ProgId query exception: Checked '{hive}\\{userChoicePath}' ({view}) -> {ex.Message}");
            return null;
        }
    }

    private static string? GetPerceivedTypeFromBase(
        RegistryKey baseKey,
        string prefixPath,
        string extension,
        string? progId,
        RegistryHive hive,
        RegistryView view)
    {
        var extensionPerceivedType = GetNamedStringValueFromBase(
            baseKey,
            prefixPath + extension,
            "PerceivedType",
            hive,
            view);
        if (!string.IsNullOrWhiteSpace(extensionPerceivedType))
        {
            return extensionPerceivedType;
        }

        if (!string.IsNullOrWhiteSpace(progId))
        {
            return GetNamedStringValueFromBase(
                baseKey,
                prefixPath + progId,
                "PerceivedType",
                hive,
                view);
        }

        return null;
    }

    private static string? GetContentTypeFromBase(
        RegistryKey baseKey,
        string prefixPath,
        string extension,
        string? progId,
        RegistryHive hive,
        RegistryView view)
    {
        var extensionContentType = GetNamedStringValueFromBase(
            baseKey,
            prefixPath + extension,
            "Content Type",
            hive,
            view);
        if (!string.IsNullOrWhiteSpace(extensionContentType))
        {
            return extensionContentType;
        }

        if (!string.IsNullOrWhiteSpace(progId))
        {
            return GetNamedStringValueFromBase(
                baseKey,
                prefixPath + progId,
                "Content Type",
                hive,
                view);
        }

        return null;
    }

    private static bool IsTextAssociation(string? perceivedType, string? contentType)
    {
        return string.Equals(perceivedType, "text", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(contentType)
                && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetNamedStringValueFromBase(RegistryKey baseKey, string path, string valueName, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var key = baseKey.OpenSubKey(path);
            var value = key?.GetValue(valueName) as string;
            LogDiag($"{valueName} query: Checked '{hive}\\{path}' ({view}) -> found value '{value ?? "null"}'");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            LogDiag($"{valueName} query exception: Checked '{hive}\\{path}' ({view}) -> {ex.Message}");
            return null;
        }
    }

    private static string? GetClsidDescription(Guid clsid, RegistryView view)
    {
        try
        {
            using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var key = classesRoot.OpenSubKey($@"CLSID\{clsid:B}");
            return key?.GetValue(null) as string;
        }
        catch (Exception ex)
        {
            LogDiag($"CLSID description query exception clsid={clsid:B} ({view}) -> {ex.Message}");
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

public sealed record ShellPreviewHandlerRegistration(
    Guid Clsid,
    string Extension,
    string? ProgId,
    string? PerceivedType,
    string? ContentType,
    RegistryHive Hive,
    RegistryView View,
    string RegistryPath,
    string SourceKind,
    string? ClsidDescription)
{
    public static ShellPreviewHandlerRegistration Empty { get; } = new(
        Guid.Empty,
        "",
        null,
        null,
        null,
        RegistryHive.ClassesRoot,
        RegistryView.Default,
        "",
        "",
        null);
}
