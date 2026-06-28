using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileKakari;

public sealed class FileEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string _fullPath = "";
    private string _kind = "";
    private DateTime _modifiedAt;
    private DateTime _createdAt;
    private DateTime _accessedAt;
    private long? _size;
    private long? _freeSpace;
    private string? _sizeTextOverride;
    private string? _modifiedTextOverride;
    private string _attributesText = "";
    private bool _isRenaming;
    private string _renameText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value))
            {
                OnPropertyChanged(nameof(BaseName));
            }
        }
    }

    public required string FullPath
    {
        get => _fullPath;
        set
        {
            if (SetField(ref _fullPath, value))
            {
                OnPropertyChanged(nameof(ParentPath));
            }
        }
    }

    public required string Kind
    {
        get => _kind;
        set => SetField(ref _kind, value);
    }

    public required DateTime ModifiedAt
    {
        get => _modifiedAt;
        set
        {
            if (SetField(ref _modifiedAt, value))
            {
                OnPropertyChanged(nameof(ModifiedAtText));
            }
        }
    }

    public required DateTime CreatedAt
    {
        get => _createdAt;
        set
        {
            if (SetField(ref _createdAt, value))
            {
                OnPropertyChanged(nameof(CreatedAtText));
            }
        }
    }

    public required DateTime AccessedAt
    {
        get => _accessedAt;
        set
        {
            if (SetField(ref _accessedAt, value))
            {
                OnPropertyChanged(nameof(AccessedAtText));
            }
        }
    }

    public required long? Size
    {
        get => _size;
        set
        {
            if (SetField(ref _size, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public required bool IsDirectory { get; init; }

    public long? FreeSpace
    {
        get => _freeSpace;
        init => _freeSpace = value;
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetField(ref _isRenaming, value);
    }

    public string RenameText
    {
        get => _renameText;
        set => SetField(ref _renameText, value);
    }

    public string SizeText => _sizeTextOverride ?? (IsDirectory || Size is null ? "" : FormatSize(Size.Value));

    public string ModifiedAtText => _modifiedTextOverride ?? (ModifiedAt == DateTime.MinValue ? "" : ModifiedAt.ToString("g"));

    public string CreatedAtText => CreatedAt == DateTime.MinValue ? "" : CreatedAt.ToString("g");

    public string AccessedAtText => AccessedAt == DateTime.MinValue ? "" : AccessedAt.ToString("g");

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.');

    public string ParentPath
    {
        get
        {
            if (string.IsNullOrEmpty(FullPath))
            {
                return "";
            }
            try
            {
                return Path.GetDirectoryName(FullPath) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    public string BaseName
    {
        get
        {
            if (IsDirectory)
            {
                return Name;
            }
            if (string.IsNullOrEmpty(Name))
            {
                return "";
            }
            if (Name.StartsWith('.') && Name.IndexOf('.', 1) < 0)
            {
                return Name;
            }
            try
            {
                return Path.GetFileNameWithoutExtension(Name);
            }
            catch
            {
                return Name;
            }
        }
    }

    public string AttributesText
    {
        get => _attributesText;
        internal set => SetField(ref _attributesText, value);
    }

    // ImageSource for the shell icon. Loaded asynchronously and cached per-extension by ShellIconProvider.
    public System.Windows.Media.ImageSource? Icon { get; private set; }

    // Resolve the shell icon before the entry is shown. ShellIconProvider caches by extension.
    public async System.Threading.Tasks.Task LoadIconAsync()
    {
        try
        {
            var img = await ShellIconProvider.Instance.GetIconAsync(IsDirectory ? null : Extension, IsDirectory, FullPath).ConfigureAwait(false);
            if (img is not null)
            {
                Icon = img;
            }
        }
        catch
        {
            // Ignore failures and leave Icon as null so UI falls back to glyphs.
        }
    }

    public static FileEntry FromDirectory(DirectoryInfo directory)
    {
        return new FileEntry
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            Kind = AppStrings.Get("TypeFolder"),
            ModifiedAt = directory.LastWriteTime,
            CreatedAt = directory.CreationTime,
            AccessedAt = directory.LastAccessTime,
            Size = null,
            IsDirectory = true,
            AttributesText = FormatAttributes(directory.Attributes)
        };
    }

    public static FileEntry FromDirectoryBasic(DirectoryInfo directory)
    {
        return new FileEntry
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            Kind = AppStrings.Get("TypeFolder"),
            ModifiedAt = directory.LastWriteTime,
            CreatedAt = DateTime.MinValue,
            AccessedAt = DateTime.MinValue,
            Size = null,
            IsDirectory = true,
            AttributesText = ""
        };
    }

    public static FileEntry FromFile(FileInfo file)
    {
        return new FileEntry
        {
            Name = file.Name,
            FullPath = file.FullName,
            Kind = string.IsNullOrWhiteSpace(file.Extension) ? AppStrings.Get("TypeFile") : file.Extension.TrimStart('.').ToUpperInvariant(),
            ModifiedAt = file.LastWriteTime,
            CreatedAt = file.CreationTime,
            AccessedAt = file.LastAccessTime,
            Size = file.Length,
            IsDirectory = false,
            AttributesText = FormatAttributes(file.Attributes)
        };
    }

    public static FileEntry FromFileBasic(FileInfo file)
    {
        return new FileEntry
        {
            Name = file.Name,
            FullPath = file.FullName,
            Kind = string.IsNullOrWhiteSpace(file.Extension) ? AppStrings.Get("TypeFile") : file.Extension.TrimStart('.').ToUpperInvariant(),
            ModifiedAt = file.LastWriteTime,
            CreatedAt = DateTime.MinValue,
            AccessedAt = DateTime.MinValue,
            Size = file.Length,
            IsDirectory = false,
            AttributesText = ""
        };
    }

    internal static string GetTypeText(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return AppStrings.Get("TypeFolder");
        }

        var extension = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return AppStrings.Get("TypeFile");
        }

        var extUpper = extension.TrimStart('.').ToUpperInvariant();
        var isJa = AppStrings.EffectiveCulture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

        return extUpper switch
        {
            "TXT" => isJa ? "テキスト ファイル" : "Text Document",
            "PNG" => isJa ? "PNG ファイル" : "PNG File",
            "JPG" or "JPEG" => isJa ? "JPEG ファイル" : "JPEG File",
            "GIF" => isJa ? "GIF ファイル" : "GIF File",
            "BMP" => isJa ? "BMP ファイル" : "BMP File",
            "EXE" => isJa ? "アプリケーション" : "Application",
            "DLL" => isJa ? "アプリケーション拡張" : "Application Extension",
            "ZIP" => isJa ? "ZIP アーカイブ" : "ZIP Archive",
            "RAR" => isJa ? "RAR アーカイブ" : "RAR Archive",
            "7Z" => isJa ? "7Z アーカイブ" : "7Z Archive",
            "JSON" => isJa ? "JSON ファイル" : "JSON File",
            "XML" => isJa ? "XML ドキュメント" : "XML Document",
            "HTML" or "HTM" => isJa ? "HTML ドキュメント" : "HTML Document",
            "CSS" => isJa ? "CSS スタイルシート" : "CSS Stylesheet",
            "JS" => isJa ? "JavaScript ソース ファイル" : "JavaScript Source File",
            "TS" => isJa ? "TypeScript ソース ファイル" : "TypeScript Source File",
            "CS" => isJa ? "C# ソース ファイル" : "C# Source File",
            "PY" => isJa ? "Python スクリプト" : "Python Script",
            "SH" or "BASH" => isJa ? "シェル スクリプト" : "Shell Script",
            "BAT" or "CMD" => isJa ? "Windows バッチ ファイル" : "Windows Batch File",
            "PDF" => isJa ? "PDF ドキュメント" : "PDF Document",
            "MP3" => isJa ? "MP3 オーディオ" : "MP3 Audio",
            "WAV" => isJa ? "WAV オーディオ" : "WAV Audio",
            "MP4" => isJa ? "MP4 ビデオ" : "MP4 Video",
            "MKV" => isJa ? "MKV ビデオ" : "MKV Video",
            "AVI" => isJa ? "AVI ビデオ" : "AVI Video",
            "MD" => isJa ? "Markdown ドキュメント" : "Markdown Document",
            "INI" or "CONF" => isJa ? "構成設定" : "Configuration Settings",
            _ => isJa ? $"{extUpper} ファイル" : $"{extUpper} File"
        };
    }

    internal static string GetAttributesText(FileAttributes attributes)
    {
        return FormatAttributes(attributes);
    }

    public static FileEntry FromDrive(DriveInfo drive)
    {
        var displayName = GetDriveDisplayName(drive);
        var sizeText = "";
        var modifiedText = AppStrings.Get("DriveNotReady");
        long? driveSize = null;
        long? freeSpaceValue = null;
        try
        {
            if (drive.IsReady)
            {
                var totalSize = drive.TotalSize;
                var freeSpace = drive.AvailableFreeSpace;
                var usedPercent = totalSize <= 0
                    ? 0
                    : (double)(totalSize - freeSpace) / totalSize * 100;
                driveSize = totalSize;
                freeSpaceValue = freeSpace;
                sizeText = FormatSize(totalSize);
                modifiedText = AppStrings.Format("DriveFreeAndUsage", FormatSize(freeSpace), usedPercent);
            }
        }
        catch
        {
            modifiedText = AppStrings.Get("DriveNotReady");
        }

        return new FileEntry
        {
            Name = displayName,
            FullPath = drive.Name,
            Kind = AppStrings.Get($"DriveType{drive.DriveType}"),
            ModifiedAt = DateTime.MinValue,
            CreatedAt = DateTime.MinValue,
            AccessedAt = DateTime.MinValue,
            Size = driveSize,
            FreeSpace = freeSpaceValue,
            IsDirectory = true,
            AttributesText = "",
            _sizeTextOverride = sizeText,
            _modifiedTextOverride = modifiedText
        };
    }

    private static string GetDriveDisplayName(DriveInfo drive)
    {
        var driveLetter = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fallbackName = string.IsNullOrWhiteSpace(driveLetter)
            ? drive.Name
            : $"({driveLetter})";

        try
        {
            if (!drive.IsReady)
            {
                return fallbackName;
            }

            var volumeLabel = drive.VolumeLabel;
            return string.IsNullOrWhiteSpace(volumeLabel)
                ? fallbackName
                : $"({driveLetter}) {volumeLabel}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return fallbackName;
        }
    }

    public void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    public void CancelRename()
    {
        RenameText = Name;
        IsRenaming = false;
    }

    public void UpdateFromPath(string path)
    {
        if (IsDirectory)
        {
            var directory = new DirectoryInfo(path);
            Name = directory.Name;
            FullPath = directory.FullName;
            Kind = AppStrings.Get("TypeFolder");
            ModifiedAt = directory.LastWriteTime;
            CreatedAt = directory.CreationTime;
            AccessedAt = directory.LastAccessTime;
            Size = null;
            AttributesText = FormatAttributes(directory.Attributes);
            RenameText = Name;
            return;
        }

        var file = new FileInfo(path);
        Name = file.Name;
        FullPath = file.FullName;
        Kind = string.IsNullOrWhiteSpace(file.Extension) ? AppStrings.Get("TypeFile") : file.Extension.TrimStart('.').ToUpperInvariant();
        ModifiedAt = file.LastWriteTime;
        CreatedAt = file.CreationTime;
        AccessedAt = file.LastAccessTime;
        Size = file.Length;
        AttributesText = FormatAttributes(file.Attributes);
        RenameText = Name;
    }

    public bool RefreshMetadataFromPath(string path)
    {
        try
        {
            if (IsDirectory)
            {
                if (!Directory.Exists(path))
                {
                    return false;
                }

                var directory = new DirectoryInfo(path);
                ModifiedAt = directory.LastWriteTime;
                Size = null;
                AttributesText = FormatAttributes(directory.Attributes);
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            var file = new FileInfo(path);
            ModifiedAt = file.LastWriteTime;
            Size = file.Length;
            AttributesText = FormatAttributes(file.Attributes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:N1} {units[unit]}";
    }

    private static string FormatAttributes(FileAttributes attributes)
    {
        var parts = new List<string>();
        if (attributes.HasFlag(FileAttributes.Hidden))
        {
            parts.Add(AppStrings.Get("AttributeHidden"));
        }

        if (attributes.HasFlag(FileAttributes.System))
        {
            parts.Add(AppStrings.Get("AttributeSystem"));
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            parts.Add(AppStrings.Get("AttributeReadOnly"));
        }

        if (attributes.HasFlag(FileAttributes.Archive))
        {
            parts.Add(AppStrings.Get("AttributeArchive"));
        }

        return string.Join(", ", parts);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
