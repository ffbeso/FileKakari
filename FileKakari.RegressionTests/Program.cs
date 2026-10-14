using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileKakari;

// Service checks only: never construct App/MainWindow or load/save the user's profile.
if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out _))
{
    Console.Error.WriteLine("Usage: FileKakari.RegressionTests <run-directory> <run-id>");
    return 2;
}

var runDirectory = Path.GetFullPath(args[0]);
var checks = new List<CheckResult>();
try
{
    Directory.CreateDirectory(runDirectory);
    var fixtures = Path.Combine(runDirectory, "fixtures");
    if (Directory.Exists(fixtures)) throw new IOException("Fixture directory already exists. Use a fresh run directory.");
    Directory.CreateDirectory(fixtures);
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    AppStrings.Configure(AppLanguageMode.Japanese);
    var textProvider = new BuiltInTextPreviewProvider();

    foreach (var sample in new[]
    {
        ("日本語 空白 & #.txt", "日本語のプレビュー\r\nsecond line", (Encoding)new UTF8Encoding(false), "utf-8"),
        ("cp932.bat", "@echo off\r\necho 接続確認", Encoding.GetEncoding(932), "cp932"),
        ("ascii.cmd", "@echo off\r\necho TEST", Encoding.ASCII, "utf-8"),
        ("readme.md", "# Markdown\n本文", new UTF8Encoding(false), "utf-8")
    })
    {
        await CheckAsync($"text-{sample.Item1}", async () =>
        {
            var path = Path.Combine(fixtures, sample.Item1);
            await File.WriteAllTextAsync(path, sample.Item2, sample.Item3);
            var preview = await textProvider.CreatePreviewAsync(new PreviewRequest(path), CancellationToken.None);
            Require(preview.Status == FilePreviewStatus.Success && preview.Kind == FilePreviewKind.Text,
                $"Expected text success, got {preview.Status}/{preview.Kind}: {preview.ErrorMessage}");
            Require(preview.Text == sample.Item2 && preview.EncodingName == sample.Item4,
                $"Text or encoding mismatch: {preview.EncodingName}");
        });
    }

    await CheckAsync("text-too-large", async () =>
    {
        var path = Path.Combine(fixtures, "large.txt");
        using (var stream = File.Create(path)) stream.SetLength(BuiltInTextPreviewProvider.MaxTextBytes + 1);
        var preview = await textProvider.CreatePreviewAsync(new PreviewRequest(path), CancellationToken.None);
        Require(preview.Status == FilePreviewStatus.TooLarge, $"Expected TooLarge, got {preview.Status}");
    });
    await CheckAsync("text-missing", async () =>
    {
        var preview = await textProvider.CreatePreviewAsync(new PreviewRequest(Path.Combine(fixtures, "missing.txt")), CancellationToken.None);
        Require(preview.Status == FilePreviewStatus.Missing, $"Expected Missing, got {preview.Status}");
    });
    await CheckAsync("text-cancelled", async () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await textProvider.CreatePreviewAsync(new PreviewRequest(Path.Combine(fixtures, "ascii.cmd")), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancelled text read completed without cancellation.");
    });
    await CheckAsync("image-decode", async () =>
    {
        var path = Path.Combine(fixtures, "image.png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var preview = await new BuiltInImagePreviewProvider().CreatePreviewAsync(
            new PreviewRequest(path) { TargetWidthDip = 2, TargetHeightDip = 2 }, CancellationToken.None);
        Require(preview.Status == FilePreviewStatus.Success && preview.ImageSource is { IsFrozen: true, PixelWidth: > 0 },
            $"Image decoding failed: {preview.Status}: {preview.ErrorMessage}");
    });
    await CheckAsync("enumeration-sort-and-metadata", () =>
    {
        var folder = Path.Combine(fixtures, "listing");
        Directory.CreateDirectory(Path.Combine(folder, "z-folder"));
        File.WriteAllText(Path.Combine(folder, "b.txt"), "bb", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a", new UTF8Encoding(false));
        var enumerator = new FileSystemEnumerator();
        var entries = enumerator.Enumerate(folder, "Name", true, true, true, CancellationToken.None).ToList();
        Require(entries.Select(e => e.Name).SequenceEqual(new[] { "z-folder", "a.txt", "b.txt" }), "Folders-first/name order changed.");
        Require(entries[0].IsDirectory && entries[1].Size == 1 && entries[2].Size == 2, "Entry metadata mismatch.");
        var descending = enumerator.Enumerate(folder, "Name", false, true, true, CancellationToken.None).ToList();
        Require(descending.Select(e => e.Name).SequenceEqual(new[] { "z-folder", "b.txt", "a.txt" }), "Descending order changed.");
        return Task.CompletedTask;
    });
    await CheckAsync("session-roundtrip", () =>
    {
        var path = Path.Combine(fixtures, "session.json");
        var service = new SessionStateService();
        var state = new SessionState
        {
            Tabs = [new SessionTabState { TabId = "test-tab", Path = fixtures, FilterText = "日本語", ScrollOffset = 42, SelectedPaths = ["a.txt"] }]
        };
        Require(service.Save(path, state), "Session save failed.");
        var loaded = service.Load(path);
        Require(loaded.IsSuccess && loaded.State!.Tabs.Count == 1, "Session load failed.");
        var tab = loaded.State!.Tabs[0];
        Require(tab.TabId == "test-tab" && tab.Path == fixtures && tab.FilterText == "日本語" &&
            tab.ScrollOffset == 42 && tab.SelectedPaths.SequenceEqual(new[] { "a.txt" }), "Session state changed during roundtrip.");
        return Task.CompletedTask;
    });
    await CheckAsync("session-invalid-json", () =>
    {
        var path = Path.Combine(fixtures, "broken-session.json");
        File.WriteAllText(path, "{ invalid");
        Require(new SessionStateService().Load(path).Status == SessionStateLoadStatus.InvalidJson, "Invalid JSON was not reported.");
        return Task.CompletedTask;
    });
}
catch (Exception ex)
{
    checks.Add(new CheckResult("setup", false, ex.ToString()));
    Console.Error.WriteLine(ex);
}

var passed = checks.Count > 0 && checks.All(check => check.Passed);
await File.WriteAllTextAsync(Path.Combine(runDirectory, "results.json"), JsonSerializer.Serialize(
    new { SchemaVersion = 1, RunId = args[1], Passed = passed, Tests = checks }, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

async Task CheckAsync(string name, Func<Task> check)
{
    try
    {
        await check();
        checks.Add(new CheckResult(name, true, null));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        checks.Add(new CheckResult(name, false, ex.ToString()));
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record CheckResult(string Name, bool Passed, string? Error);
