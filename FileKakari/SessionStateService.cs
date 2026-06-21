using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari;

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
        string path = SessionPath;
        if (!File.Exists(path))
        {
            return new SessionState();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionState>(json, JsonOptions) ?? new SessionState();
        }
        catch
        {
            return new SessionState();
        }
    }

    public void Save(SessionState state)
    {
        try
        {
            AppPaths.EnsureSettingsDirectory();

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(SessionPath, json);
        }
        catch
        {
            // Session state is a convenience feature; failure to save must not block app exit.
        }
    }
}
