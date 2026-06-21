using System.IO;

namespace FileKakari;

internal sealed class FileTransferReloadPlan
{
    public required IReadOnlyList<string> ReloadPaths { get; init; }

    public required IReadOnlyList<string> RevealTargetPaths { get; init; }

    public required string TargetDirectory { get; init; }
}

internal static class FileTransferResultService
{
    public static FileTransferReloadPlan CreateReloadPlan(
        string targetDirectory,
        IEnumerable<string> executedTargets,
        IEnumerable<string> sourcePaths,
        bool includeSourceDirectories)
    {
        var revealTargetPaths = executedTargets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reloadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            targetDirectory
        };

        if (includeSourceDirectories)
        {
            foreach (var sourcePath in sourcePaths)
            {
                var sourceDirectory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    reloadPaths.Add(sourceDirectory);
                }
            }
        }

        return new FileTransferReloadPlan
        {
            ReloadPaths = reloadPaths.ToList(),
            RevealTargetPaths = revealTargetPaths,
            TargetDirectory = targetDirectory
        };
    }
}
