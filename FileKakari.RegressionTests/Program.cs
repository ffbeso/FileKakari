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
    await CheckAsync("undo-retry-after-partial-move", async () =>
    {
        var folder = Path.Combine(fixtures, "undo");
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.txt");
        var b = Path.Combine(folder, "b.txt");
        var movedA = Path.Combine(folder, "moved-a.txt");
        var movedB = Path.Combine(folder, "moved-b.txt");
        File.WriteAllText(movedA, "a");
        File.WriteAllText(movedB, "b");
        File.WriteAllText(b, "collision");
        var undo = new UndoService(new FileOperationService());
        undo.RecordFileOperation(UndoOperationKind.Move,
            [new(a, movedA, false), new(b, movedB, false)]);
        try { await undo.UndoLastAsync(); throw new InvalidOperationException("Expected a collision."); }
        catch (IOException) { }
        Require(File.ReadAllText(a) == "a" && undo.LastAction is FileOperationUndoAction { Items.Count: 1 }, "Completed undo item retained.");
        File.Move(b, Path.Combine(folder, "collision.txt"));
        await undo.UndoLastAsync();
        Require(File.ReadAllText(b) == "b" && undo.LastAction is null, "Retry failed.");
    });
    await CheckAsync("watch-root-normalization", () =>
    {
        var root = Path.GetPathRoot(fixtures)!;
        Require(FolderWatchService.NormalizeFolderPath(root) == root, "Drive root became drive-relative.");
        Require(Path.IsPathFullyQualified(FolderWatchService.NormalizeFolderPath(root)), "Root is not fully qualified.");
        Require(FolderWatchService.NormalizeFolderPath(fixtures + Path.DirectorySeparatorChar) == fixtures, "Folder separator not normalized.");
        Require(FolderWatchService.NormalizeFolderPath(@"\\server\share\") == @"\\server\share", "UNC root changed.");
        return Task.CompletedTask;
    });
    await CheckAsync("copy-rejects-junction-cycle", async () =>
    {
        var source = Path.Combine(fixtures, "junction-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "keep.txt"), "unchanged");
        var link = Path.Combine(source, "parent-link");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; $null=New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET" }) start.ArgumentList.Add(arg);
        start.Environment["TEST_LINK"] = link;
        start.Environment["TEST_TARGET"] = source;
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, "Could not create test junction: " + await error);
        try
        {
            var result = await FileSystemOperations.CopySelfMultipleAsync([source], fixtures);
            Require(result.Error is IOException && result.ExecutedPaths.Count == 0, "Junction cycle was not rejected.");
            Require(File.ReadAllText(Path.Combine(source, "keep.txt")) == "unchanged", "Source changed.");
            Require(Directory.GetDirectories(fixtures, "junction-source*").Length == 1, "Partial copy created before rejecting junction.");
        }
        finally { Directory.Delete(link); } // Remove only the link, never recurse into its target.
    });
    await CheckAsync("enumeration-cancelled-before-io", () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        foreach (var column in new[] { "Name", "Kind", "ModifiedAt", "__none" })
        {
            try
            {
                new FileSystemEnumerator().Enumerate(Path.Combine(fixtures, "nonexistent"), column, true, true, true, cts.Token).ToList();
                throw new InvalidOperationException("Cancelled enumeration completed.");
            }
            catch (OperationCanceledException) { }
        }
        return Task.CompletedTask;
    });
    await CheckAsync("deferred-icon-notification", async () =>
    {
        var entry = FileEntry.FromFile(new FileInfo(Path.Combine(fixtures, "ascii.cmd")));
        Require(entry.Icon is null, "Entry creation eagerly loaded an icon.");
        var notified = false;
        entry.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(FileEntry.Icon);
        await entry.LoadIconAsync();
        Require(entry.Icon is not null && notified, "Deferred icon did not notify the binding.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await entry.LoadIconAsync(cancelled.Token); throw new InvalidOperationException("Cancelled icon request completed."); }
        catch (OperationCanceledException) { }
    });
    await CheckAsync("overlapping-viewstate-suppression", () =>
    {
        var suppression = new OperationSuppression();
        var first = suppression.Enter();
        var second = suppression.Enter();
        first.Dispose();
        Require(suppression.IsActive, "First completion released another operation's suppression.");
        first.Dispose();
        Require(suppression.IsActive, "Double disposal released another operation.");
        second.Dispose();
        Require(!suppression.IsActive, "Suppression leaked after completion.");
        try { using var failed = suppression.Enter(); throw new OperationCanceledException(); }
        catch (OperationCanceledException) { }
        Require(!suppression.IsActive, "Suppression leaked on cancellation.");
        return Task.CompletedTask;
    });
    await CheckAsync("atomic-save-preserves-previous-on-failure", () =>
    {
        var path = Path.Combine(fixtures, "atomic.json");
        AtomicFileWriter.WriteAllText(path, "{\"version\":1}");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { AtomicFileWriter.WriteAllText(path, "{\"version\":2}"); throw new InvalidOperationException("Expected locked destination to fail."); }
            catch (IOException) { }
        }
        Require(File.ReadAllText(path) == "{\"version\":1}", "Failed save damaged previous file.");
        Require(!Directory.EnumerateFiles(fixtures, ".atomic.json.*.tmp").Any(), "Temporary save file leaked.");
        AtomicFileWriter.WriteAllText(path, "{\"version\":3}");
        Require(File.ReadAllText(path) == "{\"version\":3}", "Atomic replacement failed.");
        return Task.CompletedTask;
    });
    await CheckAsync("session-save-request-order", async () =>
    {
        var path = Path.Combine(fixtures, "ordered-session.json");
        var service = new SessionStateService();
        var saves = Enumerable.Range(0, 20).Select(index => service.SaveAsync(path,
            new SessionState { SelectedTabIndex = index })).ToArray();
        Require((await Task.WhenAll(saves)).All(saved => saved), "Queued save failed.");
        Require(service.Load(path).State?.SelectedTabIndex == 19, "An older snapshot overwrote the latest save.");
        var queue = new SerializedTaskQueue();
        var failed = queue.Enqueue<int>(() => throw new IOException("test failure"));
        var next = queue.Enqueue(() => 42);
        try { await failed; } catch (IOException) { }
        Require(await next == 42, "A failed operation blocked the save queue.");
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
