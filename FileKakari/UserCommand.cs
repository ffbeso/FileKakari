using System;
using System.Collections.Generic;
using System.Linq;

namespace FileKakari;

public sealed class UserCommand
{
    public string? Name { get; set; }
    public string? Executable { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool UseShellExecute { get; set; }
    public string? Target { get; set; } = "Any"; // Any, Selection, CurrentDirectory

    public bool Enabled { get; set; } = true;
    public List<string> Extensions { get; set; } = new();
    public bool AllowFiles { get; set; } = true;
    public bool AllowDirectories { get; set; } = true;
    public bool AllowMultiple { get; set; } = true;

    public void Normalize()
    {
        if (Extensions == null)
        {
            Extensions = new();
            return;
        }

        Extensions = Extensions
            .Select(NormalizeExtension)
            .Where(ext => !string.IsNullOrWhiteSpace(ext))
            .Distinct()
            .ToList();
    }

    private static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        var trimmed = ext.Trim();
        if (trimmed == "*") return "*";

        if (trimmed.StartsWith("*."))
        {
            trimmed = trimmed.Substring(1); // "*.cs" -> ".cs"
        }
        else if (!trimmed.StartsWith("."))
        {
            trimmed = "." + trimmed; // "cs" -> ".cs"
        }

        return trimmed.ToLowerInvariant();
    }

    public bool ShouldShow(IReadOnlyList<FileEntry>? selectedEntries)
    {
        if (!Enabled)
        {
            return false;
        }

        if (selectedEntries == null || selectedEntries.Count == 0)
        {
            return true;
        }

        if (selectedEntries.Count >= 2 && !AllowMultiple)
        {
            return false;
        }

        bool hasFiles = selectedEntries.Any(e => !e.IsDirectory);
        if (hasFiles && !AllowFiles)
        {
            return false;
        }

        bool hasDirectories = selectedEntries.Any(e => e.IsDirectory);
        if (hasDirectories && !AllowDirectories)
        {
            return false;
        }

        if (Extensions == null || Extensions.Count == 0 || Extensions.Contains("*"))
        {
            return true;
        }

        var files = selectedEntries.Where(e => !e.IsDirectory).ToList();
        if (files.Count > 0)
        {
            foreach (var file in files)
            {
                var ext = "." + file.Extension.ToLowerInvariant();
                if (!Extensions.Contains(ext))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
