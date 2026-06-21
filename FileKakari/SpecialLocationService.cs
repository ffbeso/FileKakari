using System.IO;

namespace FileKakari;

public sealed class SpecialLocationService
{
    public const string ThisPcUri = "special://this-pc";

    public IReadOnlyList<SpecialLocation> GetLocations()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            new SpecialLocation("Home", AppStrings.Get("LocationHome"), userProfile, Directory.Exists(userProfile)),
            new SpecialLocation("ThisPc", AppStrings.Get("LocationThisPc"), ThisPcUri, true),
            CreateFolderLocation("Desktop", "LocationDesktop", Environment.SpecialFolder.DesktopDirectory),
            CreateFolderLocation("Documents", "LocationDocuments", Environment.SpecialFolder.MyDocuments),
            CreateUserFolderLocation("Downloads", "LocationDownloads", userProfile, "Downloads"),
            CreateFolderLocation("Pictures", "LocationPictures", Environment.SpecialFolder.MyPictures),
            CreateFolderLocation("Music", "LocationMusic", Environment.SpecialFolder.MyMusic),
            CreateFolderLocation("Videos", "LocationVideos", Environment.SpecialFolder.MyVideos)
        ];
    }

    public IEnumerable<FileEntry> EnumerateThisPc()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            yield return FileEntry.FromDrive(drive);
        }
    }

    public static bool IsSpecialUri(string path)
    {
        return string.Equals(path, ThisPcUri, StringComparison.OrdinalIgnoreCase);
    }

    private static SpecialLocation CreateFolderLocation(string key, string textKey, Environment.SpecialFolder specialFolder)
    {
        var path = Environment.GetFolderPath(specialFolder);
        return new SpecialLocation(key, AppStrings.Get(textKey), path, Directory.Exists(path));
    }

    private static SpecialLocation CreateUserFolderLocation(string key, string textKey, string userProfile, string folderName)
    {
        var path = Path.Combine(userProfile, folderName);
        return new SpecialLocation(key, AppStrings.Get(textKey), path, Directory.Exists(path));
    }
}
