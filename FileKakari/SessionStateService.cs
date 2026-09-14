using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari;

public enum SessionStateLoadStatus
{
    Success,
    FileNotFound,
    InvalidJson,
    IoError
}

public sealed record SessionStateLoadResult(
    SessionStateLoadStatus Status,
    SessionState? State = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == SessionStateLoadStatus.Success && State is not null;
}

public sealed class SessionStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SessionPath { get; }

    public SessionStateService()
    {
        SessionPath = AppPaths.SessionPath;
    }

    public SessionState Load()
    {
        return Load(SessionPath).State ?? new SessionState();
    }

    public SessionStateLoadResult Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new SessionStateLoadResult(SessionStateLoadStatus.FileNotFound);
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            return state is null
                ? new SessionStateLoadResult(SessionStateLoadStatus.InvalidJson, ErrorMessage: "Session JSON was empty.")
                : new SessionStateLoadResult(SessionStateLoadStatus.Success, state);
        }
        catch (JsonException ex)
        {
            return new SessionStateLoadResult(SessionStateLoadStatus.InvalidJson, ErrorMessage: ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return new SessionStateLoadResult(SessionStateLoadStatus.InvalidJson, ErrorMessage: ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return new SessionStateLoadResult(SessionStateLoadStatus.FileNotFound, ErrorMessage: ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return new SessionStateLoadResult(SessionStateLoadStatus.FileNotFound, ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            return new SessionStateLoadResult(SessionStateLoadStatus.IoError, ErrorMessage: ex.Message);
        }
    }

    public void Save(SessionState state)
    {
        Save(SessionPath, state);
    }

    public bool Save(string path, SessionState state)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(fullPath, json);
            PerfLog.WriteVerbose(
                $"json-save-complete target=\"session\" path=\"{fullPath}\" " +
                $"tabs={state.Tabs.Count} folderColumnWidthKeys={state.FolderColumnWidths.Count} columnWidths={state.ColumnWidths.Count}");
            return true;
        }
        catch (Exception ex)
        {
            PerfLog.WriteVerbose($"json-save-failed target=\"session\" path=\"{path}\" error=\"{ex.Message}\"");
            // Session state is a convenience feature; failure to save must not block app exit.
            return false;
        }
    }
}
