using System.Windows.Controls;

namespace FileKakari;

public sealed class ActiveNavigationContext
{
    public bool IsWorkspace { get; init; }
    public FolderPane? Pane { get; init; }
    public FolderTab? Tab { get; init; }
    public string? Path { get; init; }
    public ListView? ListView { get; init; }

    /// <summary>
    /// This PC などの特殊な場所以外の実体パスが存在するかどうかを示します。
    /// </summary>
    public bool HasRealPath => !string.IsNullOrWhiteSpace(Path);
}
