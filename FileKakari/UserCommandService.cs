using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari;

public sealed class UserCommandService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<UserCommand> Commands { get; private set; } = [];

    public string CommandsPath { get; }

    public UserCommandService()
    {
        CommandsPath = AppPaths.CommandsPath;
    }

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CommandsDirectory);

            if (!File.Exists(CommandsPath))
            {
                GenerateSampleCommands();
                return;
            }

            var json = File.ReadAllText(CommandsPath);
            Commands = JsonSerializer.Deserialize<List<UserCommand>>(json, JsonOptions) ?? [];
            foreach (var cmd in Commands)
            {
                cmd.Normalize();
            }
        }
        catch (Exception ex)
        {
            Commands = [];
            PerfLog.Write($"user-command-load-error path=\"{CommandsPath}\" error=\"{ex.Message}\"");
        }
    }

    private void GenerateSampleCommands()
    {
        Commands =
        [
            new UserCommand
            {
                Name = "Open in Notepad",
                Executable = "notepad.exe",
                Arguments = "\"{selectedFile}\"",
                WorkingDirectory = "{currentDirectory}",
                UseShellExecute = true,
                Target = "Selection"
            },
            new UserCommand
            {
                Name = "Open Command Prompt Here",
                Executable = "cmd.exe",
                Arguments = "/K cd /d \"{currentDirectory}\"",
                WorkingDirectory = "{currentDirectory}",
                UseShellExecute = true,
                Target = "CurrentDirectory"
            }
        ];

        foreach (var cmd in Commands)
        {
            cmd.Normalize();
        }

        try
        {
            AppPaths.EnsureSettingsDirectory();
            var json = JsonSerializer.Serialize(Commands, JsonOptions);
            File.WriteAllText(CommandsPath, json);
        }
        catch (Exception ex)
        {
            PerfLog.Write($"user-command-save-sample-error path=\"{CommandsPath}\" error=\"{ex.Message}\"");
        }
    }

    public void Save(List<UserCommand> commands)
    {
        Commands = commands ?? [];
        foreach (var cmd in Commands)
        {
            cmd.Normalize();
        }

        try
        {
            AppPaths.EnsureSettingsDirectory();
            var json = JsonSerializer.Serialize(Commands, JsonOptions);
            File.WriteAllText(CommandsPath, json);
        }
        catch (Exception ex)
        {
            PerfLog.Write($"user-command-save-error path=\"{CommandsPath}\" error=\"{ex.Message}\"");
            throw;
        }
    }
}
