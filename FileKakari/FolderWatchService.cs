using System.IO;
using System.Threading;

namespace FileKakari;

public sealed class FolderWatchService : IDisposable
{
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private readonly Dictionary<string, WatcherState> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<string>? Changed;

    public event Action<IReadOnlyList<string>>? FileMetadataChanged;

    public event Action<string>? ChangeObserved;

    public event Action<string, Exception?>? WatchError;

    public IReadOnlyList<string> CurrentPaths
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Keys.ToList();
            }
        }
    }

    public void Start(string path)
    {
        SetWatchedFolders([path]);
    }

    public void SetWatchedFolders(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var requestedPaths = paths
                .Where(CanWatchFolder)
                .Select(NormalizeFolderPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var path in _watchers.Keys.Where(path => !requestedPaths.Contains(path)).ToList())
            {
                StopLocked(path);
            }

            foreach (var path in requestedPaths)
            {
                if (_watchers.ContainsKey(path))
                {
                    continue;
                }

                StartLocked(path);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var path in _watchers.Keys.ToList())
            {
                StopLocked(path);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }
    }

    public static bool CanWatchFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady
                && (drive.DriveType == DriveType.Fixed
                    || drive.DriveType == DriveType.Removable
                    || drive.DriveType == DriveType.Ram);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void StartLocked(string path)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime
            };
            var state = new WatcherState(path, watcher);
            _watchers.Add(path, state);
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StopLocked(path);
            WatchError?.Invoke(path, ex);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Changed)
        {
            QueueFileMetadataChanged(sender, e.FullPath);
        }
        else
        {
            QueueChanged(sender, e.FullPath);
        }

        ChangeObserved?.Invoke(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        QueueChanged(sender, e.FullPath);
        ChangeObserved?.Invoke(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        string? failedPath = null;
        lock (_gate)
        {
            var state = FindStateLocked(sender);
            if (state is not null)
            {
                failedPath = state.Path;
                StopLocked(state.Path);
            }
        }

        if (!string.IsNullOrWhiteSpace(failedPath))
        {
            WatchError?.Invoke(failedPath, e.GetException());
        }
    }

    private void QueueChanged(object sender, string changedPath)
    {
        lock (_gate)
        {
            var state = FindStateLocked(sender);
            if (state is null)
            {
                return;
            }

            state.PendingChangedPath = string.IsNullOrWhiteSpace(changedPath) ? state.Path : changedPath;
            state.DebounceTimer ??= new Timer(OnDebounceElapsed, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            state.DebounceTimer.Change(RefreshDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void QueueFileMetadataChanged(object sender, string changedPath)
    {
        lock (_gate)
        {
            var state = FindStateLocked(sender);
            if (state is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(changedPath))
            {
                state.PendingMetadataChangedPaths.Add(changedPath);
            }

            state.DebounceTimer ??= new Timer(OnDebounceElapsed, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            state.DebounceTimer.Change(RefreshDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? stateObject)
    {
        string? changedPath;
        List<string> metadataChangedPaths;
        lock (_gate)
        {
            if (stateObject is not WatcherState state || !_watchers.ContainsKey(state.Path))
            {
                return;
            }

            changedPath = state.PendingChangedPath;
            metadataChangedPaths = state.PendingMetadataChangedPaths.ToList();
            state.PendingChangedPath = null;
            state.PendingMetadataChangedPaths.Clear();
        }

        if (!string.IsNullOrWhiteSpace(changedPath))
        {
            Changed?.Invoke(changedPath);
            return;
        }

        if (metadataChangedPaths.Count > 0)
        {
            FileMetadataChanged?.Invoke(metadataChangedPaths);
        }
    }

    private WatcherState? FindStateLocked(object sender)
    {
        return _watchers.Values.FirstOrDefault(state => ReferenceEquals(state.Watcher, sender));
    }

    private void StopLocked(string path)
    {
        if (!_watchers.Remove(path, out var state))
        {
            return;
        }

        state.DebounceTimer?.Dispose();
        state.PendingChangedPath = null;
        state.PendingMetadataChangedPaths.Clear();
        state.Watcher.EnableRaisingEvents = false;
        state.Watcher.Created -= OnChanged;
        state.Watcher.Deleted -= OnChanged;
        state.Watcher.Changed -= OnChanged;
        state.Watcher.Renamed -= OnRenamed;
        state.Watcher.Error -= OnError;
        state.Watcher.Dispose();
    }

    private static string NormalizeFolderPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class WatcherState(string path, FileSystemWatcher watcher)
    {
        public string Path { get; } = path;

        public FileSystemWatcher Watcher { get; } = watcher;

        public Timer? DebounceTimer { get; set; }

        public string? PendingChangedPath { get; set; }

        public HashSet<string> PendingMetadataChangedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
