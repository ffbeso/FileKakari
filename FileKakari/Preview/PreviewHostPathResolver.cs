using System;
using System.IO;

namespace FileKakari.Preview;

public static class PreviewHostPathResolver
{
    private const string ExecutableName = "FileKakari.PreviewHost.exe";

    public static string? ResolvePreviewHostPath()
    {
        // 0. Check in directory containing the actual process executable (Single-File / SelfExtract support)
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            try
            {
                var processDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrEmpty(processDir))
                {
                    var processDirCandidate = Path.Combine(processDir, ExecutableName);
                    if (File.Exists(processDirCandidate))
                    {
                        return processDirCandidate;
                    }
                }
            }
            catch
            {
                // Ignore path resolution errors
            }
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Check in current application directory (Publish / same dir)
        var sameDirCandidate = Path.Combine(baseDir, ExecutableName);
        if (File.Exists(sameDirCandidate))
        {
            return sameDirCandidate;
        }

        // 2. Check in sibling build output directory (Development / Debug)
        try
        {
            var dirInfo = new DirectoryInfo(baseDir);
            if (dirInfo.Parent?.Parent is { Exists: true } binDir)
            {
                var siblingCandidate = Path.Combine(binDir.FullName, "FileKakari.PreviewHost", dirInfo.Name, ExecutableName);
                if (File.Exists(siblingCandidate))
                {
                    return siblingCandidate;
                }
            }

            // Direct sibling directory check
            var projectSiblingCandidate = Path.GetFullPath(Path.Combine(baseDir, "..", "FileKakari.PreviewHost", ExecutableName));
            if (File.Exists(projectSiblingCandidate))
            {
                return projectSiblingCandidate;
            }
        }
        catch
        {
            // Ignore path navigation errors
        }

        return null;
    }
}
