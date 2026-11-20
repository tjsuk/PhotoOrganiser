using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoOrganiser;

// Use a unique scratch directory so a run never touches the user's photo library.
var root = Directory.CreateDirectory(Path.Combine(args.Length == 0 ? Path.GetTempPath() : args[0],
    $"PhotoOrganiserTests-{Guid.NewGuid():N}")).FullName;
try
{
    var source = Path.Combine(root, "source");
    var destination = Path.Combine(root, "destination");
    var targetMonth = Path.Combine(destination, "2017", "06");
    var logs = Path.Combine(root, "logs");
    var organiser = new Organiser(logs);
    Directory.CreateDirectory(targetMonth);
    foreach (var name in new[] { "a", "b", "c", "deep/sub" })
        Directory.CreateDirectory(Path.Combine(source, name));

    // Write real JPEG metadata and set a conflicting filesystem date to test precedence.
    void Jpeg(string path, byte red, string? taken = null)
    {
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat(new byte[] { red, 30, 80 }, 64).SelectMany(pixel => pixel).ToArray(), 24);
        var metadata = new BitmapMetadata("jpg");
        if (taken is not null)
        {
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", taken);
            metadata.SetQuery("/app1/ifd/exif/{ushort=36868}", "2019:02:03 09:30:00");
        }
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        File.SetLastWriteTime(path, new DateTime(2024, 11, 4, 12, 0, 0));
    }

    var first = Path.Combine(source, "a", "photo.jpg");
    var second = Path.Combine(source, "b", "photo.jpg");
    var duplicate = Path.Combine(source, "c", "renamed.JPG");
    var fallback = Path.Combine(source, "deep", "sub", "fallback.jpg");
    var existing = Path.Combine(targetMonth, "photo.jpg");
    var beforePreview = Path.Combine(targetMonth, "other-name.jpg");
    const string capture = "2017:06:30 23:59:00";
    Jpeg(first, 110, capture);
    Jpeg(second, 160, capture);
    File.Copy(first, duplicate);
    Jpeg(fallback, 200);
    Jpeg(existing, 10);
    Jpeg(beforePreview, 60);
    File.Copy(beforePreview, Path.Combine(source, "already-present.jpg"));
    File.WriteAllText(Path.Combine(source, "notes.txt"), "ignore me");

    byte[] Digest(string file) => SHA256.HashData(File.ReadAllBytes(file));
    var originalExistingHash = Digest(existing);
    var firstHash = Digest(first);
    Preview Scan() => organiser.Scan(source, destination, CancellationToken.None);
    var updates = new List<OperationProgress>();
    var plan = organiser.Scan(source, destination, CancellationToken.None,
        new InlineProgress(updates.Add));
    Check(updates.Any(update => update is { Phase: "Finding source photos", Total: null }) &&
        updates.Any(update => update is { Phase: "Checking destination photos", Total: null } &&
            update.Path == existing), "discovery and destination progress");
    var sourceUpdates = updates.Where(update => update.Phase == "Reading source photos").ToArray();
    Check(sourceUpdates.Length == 5 && sourceUpdates.All(update => update.Total == 5) &&
        sourceUpdates.Select(update => update.Completed).SequenceEqual(Enumerable.Range(0, 5)) &&
        sourceUpdates.Any(update => update.Path == fallback), "source progress includes file paths and counts");
    Check(plan.Entries.Count == 5, "recursive discovery and extension filter");
    var topLevel = organiser.Scan(source, destination, CancellationToken.None, includeSubfolders: false);
    Check(topLevel.Entries is [{ Action: "Duplicate" } topRow] &&
        topRow.Source == Path.Combine(source, "already-present.jpg") && topRow.Destination == beforePreview,
        "top-level-only scan excludes nested sources but detects nested destination duplicates");
    Entry Row(string path) => plan.Entries.Single(row => row.Source == path);
    Check(Row(first).Date == "2017-06-30 23:59:00" && Row(first).DateSource == "EXIF DateTimeOriginal",
        "original capture beats digitization and filesystem time");
    Check(Row(fallback).DateSource == "File modified" &&
        Row(fallback).Destination.EndsWith(Path.Combine("2024", "11", "fallback.jpg")), "filesystem fallback");
    Check(Row(first).Destination == Path.Combine(targetMonth, "photo (2).jpg") &&
        Row(second).Destination == Path.Combine(targetMonth, "photo (3).jpg"), "existing and planned name collisions");
    Check(Row(duplicate).Action == "Duplicate" &&
        Row(Path.Combine(source, "already-present.jpg")).Action == "Duplicate", "duplicate lookup by content across names");
    Check(Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length == 2 &&
        !Directory.Exists(logs), "preview does not write");

    // Occupy a planned filename after preview to exercise the no-overwrite path.
    var concurrent = Row(first).Destination;
    Jpeg(concurrent, 30);
    var concurrentHash = Digest(concurrent);
    updates.Clear();
    var copy = organiser.Copy(plan, CancellationToken.None, new InlineProgress(updates.Add));
    var copyUpdates = updates.Where(update => update.Phase == "Processing copy plan").ToArray();
    Check(copyUpdates.Select(update => update.Path).SequenceEqual(plan.Entries.Select(row => row.Source)) &&
        copyUpdates.All(update => update.Total == plan.Entries.Count) &&
        copyUpdates.Select(update => update.Completed).SequenceEqual(Enumerable.Range(0, plan.Entries.Count)),
        "copy progress follows preview paths and counts");
    Check(copy.Copied == 3 && copy.Skipped == 2 && copy.Errors == 0, "copy counts");
    Check(Digest(existing).SequenceEqual(originalExistingHash) &&
        Digest(concurrent).SequenceEqual(concurrentHash), "no overwrite");
    Check(Digest(first).SequenceEqual(firstHash) &&
        File.Exists(duplicate) && File.Exists(fallback), "sources preserved");
    Check(Directory.GetFiles(targetMonth).Any(path => Digest(path).SequenceEqual(firstHash)),
        "published copy matches original bytes");
    Check(Scan().Entries.All(row => row.Action == "Duplicate"), "rerun detects copied photos");
    Check(File.ReadLines(copy.LogPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
        .Last().GetProperty("status").GetString() == "Finished", "structured finish log");

    // Replace a source after scanning to verify the staged bytes are checked against the plan.
    var changing = Path.Combine(source, "a", "changing.jpg");
    Jpeg(changing, 250, capture);
    plan = Scan();
    var changedTarget = Row(changing).Destination;
    Jpeg(changing, 251, capture);
    var changed = organiser.Copy(plan, CancellationToken.None);
    Check(changed.Errors == 1 && !File.Exists(changedTarget), "changed source rejected");

    // Cancel as the copy phase starts and inspect both the destination and the run log.
    var large = Path.Combine(source, "0.jpg");
    using (var output = File.Create(large))
        output.SetLength(16 * 1024 * 1024);
    plan = Scan();
    using var stop = new CancellationTokenSource();
    var largeTarget = Row(large).Destination;
    var cancel = new InlineProgress(update =>
    {
        if (update.Phase == "Processing copy plan" && update.Path == large)
            stop.Cancel();
    });
    var interrupted = organiser.Copy(plan, stop.Token, cancel);
    Check(interrupted.Cancelled && !File.Exists(largeTarget), "copy cancellation");
    Check(!Directory.EnumerateFiles(destination, "*.tmp", SearchOption.AllDirectories).Any(), "staging cleaned up");
    Check(File.ReadAllText(interrupted.LogPath).Contains("\"status\":\"Cancelled\""), "cancellation logged");

    using var scanStop = new CancellationTokenSource();
    scanStop.Cancel();
    Throws<OperationCanceledException>(() => organiser.Scan(source, destination, scanStop.Token), "scan cancellation");
    Throws<IOException>(() => organiser.Scan(source, Path.Combine(source, "nested"), CancellationToken.None),
        "overlapping folder rejection");
    var moveSource = Path.Combine(root, "move-source");
    var moveDestination = Path.Combine(root, "move-destination");
    Directory.CreateDirectory(moveSource);
    var toMove = Path.Combine(moveSource, "a-move.jpg");
    var duplicateToKeep = Path.Combine(moveSource, "b-duplicate.jpg");
    var changedToKeep = Path.Combine(moveSource, "c-changed.jpg");
    Jpeg(toMove, 91, capture);
    File.Copy(toMove, duplicateToKeep);
    Jpeg(changedToKeep, 95, capture);
    var expectedMove = Digest(toMove);
    var movePlan = organiser.Scan(moveSource, moveDestination, CancellationToken.None);
    var expectedTarget = movePlan.Entries.Single(row => row.Source == toMove).Destination;
    Jpeg(changedToKeep, 96, capture);
    var moveResult = organiser.Move(movePlan, CancellationToken.None);
    Check(moveResult.Moved == 1 && moveResult.Copied == 0 && moveResult.Skipped == 1 && moveResult.Errors == 1,
        "move counts successful transfers, duplicates and changed sources");
    Check(!File.Exists(toMove) && Digest(expectedTarget).SequenceEqual(expectedMove),
        "move publishes verified bytes and removes the original");
    Check(File.Exists(duplicateToKeep) && File.Exists(changedToKeep), "move keeps duplicates and changed originals");
    Check(File.ReadAllText(moveResult.LogPath).Contains("\"status\":\"Moved\""), "move is logged");

    var remainingPlan = organiser.Scan(moveSource, moveDestination, CancellationToken.None);
    using var moveStop = new CancellationTokenSource();
    var cancelledMove = organiser.Move(remainingPlan, moveStop.Token, new InlineProgress(update =>
    {
        if (update is { Phase: "Processing move plan" } && update.Path == changedToKeep)
            moveStop.Cancel();
    }));
    Check(cancelledMove.Cancelled && cancelledMove.Moved == 0 && File.Exists(changedToKeep),
        "cancelled move preserves the original");

    var controller = new PhotoController(organiser);
    var idleProgress = new InlineProgress(_ => { });
    Throws<InvalidOperationException>(() => controller.TransferAsync(false, true, CancellationToken.None, idleProgress),
        "controller requires a reviewed preview");
    var reviewed = controller.BuildPreviewAsync(moveSource, moveDestination, false, CancellationToken.None,
        idleProgress).GetAwaiter().GetResult();
    controller.Review(reviewed);
    Throws<InvalidOperationException>(() => controller.TransferAsync(true, false, CancellationToken.None, idleProgress),
        "controller requires explicit move confirmation");
    Check(controller.CanTransfer && File.Exists(changedToKeep), "unconfirmed move preserves the preview and original");
    var controllerCopy = controller.TransferAsync(false, true, CancellationToken.None, idleProgress);
    Check(!controller.CanTransfer && controllerCopy.GetAwaiter().GetResult().Copied == 1 &&
        File.Exists(changedToKeep), "controller consumes the preview and dispatches copy");
    controller.Review(reviewed);
    controller.ClearPreview();
    Check(!controller.CanTransfer, "controller clears a stale preview");

    CheckWindowResources();
    Console.WriteLine("All integration checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void CheckWindowResources()
{
    // Load the real window on an STA thread to exercise compiled XAML and packaged image resources.
    Exception? failure = null;
    var ui = new Thread(() =>
    {
        try
        {
            var window = new MainWindow();
            Check(window.Icon is BitmapSource { PixelWidth: > 0 } &&
                window.FindName("HeaderImage") is System.Windows.Controls.Image
                { Source: BitmapSource { PixelWidth: > 0 } }, "window icon and header image load");
            Check(window.FindName("AboutButton") is System.Windows.Controls.Button &&
                window.FindName("OperationBar") is System.Windows.Controls.ProgressBar &&
                window.FindName("CurrentFile") is System.Windows.Controls.TextBlock,
                "About and progress controls load");
            Check(window.FindName("IncludeSubfolders") is System.Windows.Controls.CheckBox { IsChecked: true },
                "source subfolder checkbox defaults to checked");
            Check(window.FindName("CopyButton") is System.Windows.Controls.Button { Background: SolidColorBrush copy } &&
                copy.Color == Color.FromRgb(24, 92, 182) &&
                window.FindName("MoveButton") is System.Windows.Controls.Button { Background: SolidColorBrush move } &&
                move.Color == Color.FromRgb(179, 38, 52), "copy is blue and move is red");
            window.Close();
        }
        catch (Exception error)
        {
            failure = error;
        }
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    if (failure is not null)
        throw new InvalidOperationException("Window resource check failed.", failure);
}

static void Check(bool passed, string name)
{
    if (!passed)
        throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

static void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T)
    {
        Check(true, name);
        return;
    }
    Check(false, name);
}

// Dispatch progress inline so the test can request cancellation at a known phase.
sealed class InlineProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => report(value);
}
