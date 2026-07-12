using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FileKakari;

public static class ShellPreviewHandlerRegistry
{
    private const string PreviewHandlerGuid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private static readonly Guid WindowsTxtPreviewerClsid = new("1531D583-8375-4D3F-B5FB-D23BBD169F22");
    private static readonly Guid MonacoPreviewHandlerClsid = new("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA");
    private static readonly Guid PreviewHandlerIid = new("8895b1c6-b41f-4c1c-a562-0d564250836f");
    private static readonly Guid InitializeWithFileIid = new("B7D14566-0509-4CCE-A71F-0A554233BD9B");

    private static void LogDiag(string message)
    {
        PreviewDiagnostics.Verbose("PreviewShell", message);
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _callCounters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShellPreviewHandlerRegistration> ResolvedCache = new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearCache()
    {
        ResolvedCache.Clear();
    }

    private static string NormalizeExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.')
            ? extension.ToLowerInvariant()
            : $".{extension.ToLowerInvariant()}";
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

        var normalizedExt = NormalizeExtension(filePath);

        if (ResolvedCache.TryGetValue(normalizedExt, out var cached))
        {
            if (PreviewDiagnostics.IsVerboseEnabled)
            {
                var calls = _callCounters.AddOrUpdate(normalizedExt, 1, (_, count) => count + 1);
                PreviewDiagnostics.Verbose("PreviewShell", $"Handler cache hit\r\next=\"{normalizedExt}\"\r\nclsid=\"{cached.Clsid:B}\"\r\ncallsForExtension={calls}");
            }
            registration = cached;
            return true;
        }

        var callsCount = _callCounters.AddOrUpdate(normalizedExt, 1, (_, count) => count + 1);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var resolved = TryResolvePreviewHandlerInternal(filePath, normalizedExt, out registration);
        stopwatch.Stop();

        if (PreviewDiagnostics.IsVerboseEnabled)
        {
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var clsidStr = resolved ? registration.Clsid.ToString("B") : "null";
            PreviewDiagnostics.Verbose("PreviewShell", $"Registry resolve\r\next=\"{normalizedExt}\"\r\ncache=\"miss\"\r\nresolved={resolved.ToString().ToLowerInvariant()}\r\nclsid=\"{clsidStr}\"\r\nelapsedMs={elapsedMs:F1}\r\ncallsForExtension={callsCount}");
        }

        if (resolved)
        {
            ResolvedCache.TryAdd(normalizedExt, registration);
        }

        return resolved;
    }

    private static bool TryResolvePreviewHandlerInternal(string filePath, string extension, out ShellPreviewHandlerRegistration registration)
    {
        registration = ShellPreviewHandlerRegistration.Empty;
        LogDiag($"TryGetPreviewHandlerClsid starting lookup for extension '{extension}' (file: '{filePath}')");

        if (TryGetPreviewHandlerFromAssociationApi(extension, out registration))
        {
            LogDiag($"Success: Found CLSID {registration.Clsid} using AssocQueryString for extension '{extension}'");
            return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
        }

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
                        return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
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
                            return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
                        }
                    }

                    // Rule 3: {prefix}\SystemFileAssociations\.ext\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
                    var systemPath = prefixPath + $@"SystemFileAssociations\{extension}\shellex\{PreviewHandlerGuid}";
                    if (TryGetClsidFromKeyWithDiag(baseKey, systemPath, loc.Hive, view, out clsid))
                    {
                        registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, systemPath, "system-extension");
                        LogDiag($"Success: Found CLSID {clsid} using SystemFileAssociations path: '{loc.Hive}\\{systemPath}' ({view} view)");
                        return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
                    }

                    if (!string.IsNullOrEmpty(perceivedType))
                    {
                        var perceivedPath = prefixPath + $@"SystemFileAssociations\{perceivedType}\shellex\{PreviewHandlerGuid}";
                        if (TryGetClsidFromKeyWithDiag(baseKey, perceivedPath, loc.Hive, view, out clsid))
                        {
                            registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, perceivedPath, "system-perceived-type");
                            LogDiag($"Success: Found CLSID {clsid} using PerceivedType '{perceivedType}' path: '{loc.Hive}\\{perceivedPath}' ({view} view)");
                            return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
                        }
                    }

                    var textPath = prefixPath + $@"SystemFileAssociations\text\shellex\{PreviewHandlerGuid}";
                    if (IsTextAssociation(perceivedType, contentType)
                         && TryGetClsidFromKeyWithDiag(baseKey, textPath, loc.Hive, view, out clsid))
                    {
                        registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, textPath, "system-text");
                        LogDiag($"Success: Found CLSID {clsid} using SystemFileAssociations text path: '{loc.Hive}\\{textPath}' ({view} view)");
                        return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
                    }

                    var wildcardPath = prefixPath + $@"*\shellex\{PreviewHandlerGuid}";
                    if (TryGetClsidFromKeyWithDiag(baseKey, wildcardPath, loc.Hive, view, out clsid))
                    {
                        registration = CreateRegistration(clsid, extension, progId, perceivedType, contentType, loc.Hive, view, wildcardPath, "wildcard");
                        LogDiag($"Success: Found CLSID {clsid} using wildcard path: '{loc.Hive}\\{wildcardPath}' ({view} view)");
                        return TryApplyCmdBatMonacoCompatibility(extension, registration, out registration);
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

    private static bool TryGetPreviewHandlerFromAssociationApi(string extension, out ShellPreviewHandlerRegistration registration)
    {
        registration = ShellPreviewHandlerRegistration.Empty;
        var buffer = new StringBuilder(128);
        var length = buffer.Capacity;
        var hr = AssocQueryString(
            AssocF.None,
            AssocStr.ShellExtension,
            extension,
            PreviewHandlerGuid,
            buffer,
            ref length);

        if (hr == HResultFromWin32InsufficientBuffer && length > buffer.Capacity)
        {
            buffer = new StringBuilder(length);
            hr = AssocQueryString(
                AssocF.None,
                AssocStr.ShellExtension,
                extension,
                PreviewHandlerGuid,
                buffer,
                ref length);
        }

        LogDiag($"AssocQueryString: ext=\"{extension}\" extra=\"{PreviewHandlerGuid}\" HRESULT=0x{hr:X8} length={length} value=\"{buffer}\"");
        if (hr != 0)
        {
            return false;
        }

        var value = buffer.ToString();
        if (!Guid.TryParse(value, out var clsid))
        {
            LogDiag($"AssocQueryString returned value '{value}' but it failed to parse as GUID.");
            return false;
        }

        var description = GetClsidDescription(clsid, RegistryView.Registry64)
            ?? GetClsidDescription(clsid, RegistryView.Registry32);
        registration = new ShellPreviewHandlerRegistration(
            clsid,
            extension,
            null,
            null,
            null,
            RegistryHive.ClassesRoot,
            RegistryView.Default,
            $"AssocQueryString:{extension}:{PreviewHandlerGuid}",
            "assoc-query",
            description);
        return true;
    }

    private static bool TryApplyCmdBatMonacoCompatibility(
        string extension,
        ShellPreviewHandlerRegistration resolved,
        out ShellPreviewHandlerRegistration effective)
    {
        effective = resolved;
        if (!IsCmdOrBat(extension) || resolved.Clsid != WindowsTxtPreviewerClsid)
        {
            return true;
        }

        LogDiag(
            $"CmdBat Monaco compatibility: original ext=\"{extension}\" clsid=\"{resolved.Clsid:B}\" description=\"{resolved.ClsidDescription ?? ""}\" sourceKind=\"{resolved.SourceKind}\" source=\"{resolved.Hive}\\{resolved.RegistryPath}\"");

        if (!IsMonacoPreviewHandlerUsable(out var reason))
        {
            LogDiag($"CmdBat Monaco compatibility: Monaco unavailable; suppressing Windows TXT Previewer shell candidate ext=\"{extension}\" reason=\"{reason}\" fallback=\"BuiltInTextPreviewProvider\"");
            effective = ShellPreviewHandlerRegistration.Empty;
            return false;
        }

        var monacoDescription = GetClsidDescription(MonacoPreviewHandlerClsid, RegistryView.Registry64)
            ?? GetClsidDescription(MonacoPreviewHandlerClsid, RegistryView.Registry32)
            ?? "MonacoPreviewHandler";
        effective = resolved with
        {
            Clsid = MonacoPreviewHandlerClsid,
            RegistryPath = $"cmd-bat-monaco-compat:{resolved.RegistryPath}",
            SourceKind = "cmd-bat-monaco-compat",
            ClsidDescription = monacoDescription
        };

        LogDiag(
            $"CmdBat Monaco compatibility: substituted ext=\"{extension}\" originalClsid=\"{resolved.Clsid:B}\" finalClsid=\"{effective.Clsid:B}\" description=\"{effective.ClsidDescription}\" reason=\"Windows TXT Previewer has no supported initialization interface\"");
        return true;
    }

    private static bool IsCmdOrBat(string extension)
    {
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMonacoPreviewHandlerUsable(out string reason)
    {
        reason = "";
        Type? comType;
        try
        {
            comType = Type.GetTypeFromCLSID(MonacoPreviewHandlerClsid, throwOnError: false);
        }
        catch (Exception ex)
        {
            reason = $"Type.GetTypeFromCLSID failed: {ex.Message}";
            LogDiag($"CmdBat Monaco compatibility: COM registration check clsid=\"{MonacoPreviewHandlerClsid:B}\" result=\"exception\" reason=\"{reason}\"");
            return false;
        }

        if (comType is null)
        {
            reason = "Monaco CLSID is not registered";
            LogDiag($"CmdBat Monaco compatibility: COM registration check clsid=\"{MonacoPreviewHandlerClsid:B}\" result=\"missing\"");
            return false;
        }

        LogDiag($"CmdBat Monaco compatibility: COM registration check clsid=\"{MonacoPreviewHandlerClsid:B}\" result=\"registered\" type=\"{comType.FullName}\"");

        object? instance = null;
        IntPtr pUnk = IntPtr.Zero;
        try
        {
            instance = Activator.CreateInstance(comType);
            if (instance is null)
            {
                reason = "Activator.CreateInstance returned null";
                LogDiag($"CmdBat Monaco compatibility: COM create clsid=\"{MonacoPreviewHandlerClsid:B}\" result=\"null\"");
                return false;
            }

            pUnk = Marshal.GetIUnknownForObject(instance);
            var previewHr = Marshal.QueryInterface(pUnk, in PreviewHandlerIid, out var previewPtr);
            LogDiag($"CmdBat Monaco compatibility: QueryInterface IPreviewHandler clsid=\"{MonacoPreviewHandlerClsid:B}\" HRESULT=0x{previewHr:X8}");
            if (previewPtr != IntPtr.Zero)
            {
                Marshal.Release(previewPtr);
            }

            var fileHr = Marshal.QueryInterface(pUnk, in InitializeWithFileIid, out var filePtr);
            LogDiag($"CmdBat Monaco compatibility: QueryInterface IInitializeWithFile clsid=\"{MonacoPreviewHandlerClsid:B}\" HRESULT=0x{fileHr:X8}");
            if (filePtr != IntPtr.Zero)
            {
                Marshal.Release(filePtr);
            }

            if (previewHr != 0)
            {
                reason = $"Monaco does not expose IPreviewHandler HRESULT=0x{previewHr:X8}";
                return false;
            }

            if (fileHr != 0)
            {
                reason = $"Monaco does not expose IInitializeWithFile HRESULT=0x{fileHr:X8}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"Monaco COM activation or QueryInterface failed: HRESULT=0x{ex.HResult:X8} {ex.Message}";
            LogDiag($"CmdBat Monaco compatibility: COM usability check exception clsid=\"{MonacoPreviewHandlerClsid:B}\" reason=\"{reason}\"");
            return false;
        }
        finally
        {
            if (pUnk != IntPtr.Zero)
            {
                Marshal.Release(pUnk);
            }

            if (instance is not null && Marshal.IsComObject(instance))
            {
                try
                {
                    Marshal.ReleaseComObject(instance);
                }
                catch (Exception ex)
                {
                    LogDiag($"CmdBat Monaco compatibility: ReleaseComObject exception clsid=\"{MonacoPreviewHandlerClsid:B}\" reason=\"{ex.Message}\"");
                }
            }
        }
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

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int AssocQueryString(
        AssocF flags,
        AssocStr str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder pszOut,
        ref int pcchOut);

    [Flags]
    private enum AssocF
    {
        None = 0
    }

    private enum AssocStr
    {
        ShellExtension = 16
    }

    private const int HResultFromWin32InsufficientBuffer = unchecked((int)0x8007007A);
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
