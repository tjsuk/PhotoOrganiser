using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Win32;

namespace PhotoOrganiser;

public partial class MainWindow : Window
{
    private readonly PhotoController controller = new(new Organiser());
    // Track the active operation so the view can coordinate progress, cancellation, and controls.
    private CancellationTokenSource? active;
    private string? logPath;

    public MainWindow()
    {
        InitializeComponent();
        UpdateButtons();
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var field = sender == SourceBrowse ? SourceBox : DestinationBox;
        var picker = new OpenFolderDialog { Title = sender == SourceBrowse ? "Source folder" : "Destination folder" };
        if (System.IO.Directory.Exists(field.Text))
            picker.InitialDirectory = field.Text;
        if (picker.ShowDialog(this) == true)
            field.Text = picker.FolderName;
    }

    private void PreviewOptionsChanged(object sender, RoutedEventArgs e)
    {
        // Folder and checkbox events can fire while WPF is constructing named controls.
        if (Table is null)
            return;
        controller.ClearPreview();
        Table.ItemsSource = null;
        SetStatus("Run a preview with the current settings.");
        UpdateButtons();
    }

    private async void Scan(object sender, RoutedEventArgs e)
    {
        // Read control values on the UI thread before handing the scan to a worker.
        var source = SourceBox.Text;
        var destination = DestinationBox.Text;
        var includeSubfolders = IncludeSubfolders.IsChecked == true;
        controller.ClearPreview();
        Table.ItemsSource = null;
        await Run("Scanning folders…", (token, progress) => controller.BuildPreviewAsync(source, destination, includeSubfolders, token, progress),
            result =>
            {
                controller.Review(result);
                Table.ItemsSource = result.Entries;
                var copy = result.Entries.Count(row => row.Action == "Copy");
                var duplicate = result.Entries.Count(row => row.Action == "Duplicate");
                var other = result.Entries.Count - copy - duplicate;
                SetStatus($"Preview ready · {copy:N0} to copy · {duplicate:N0} duplicates · {other:N0} other entries");
            });
    }

    private async void Copy(object sender, RoutedEventArgs e) => await Transfer(move: false);

    private async void Move(object sender, RoutedEventArgs e) => await Transfer(move: true);

    private async Task Transfer(bool move)
    {
        if (!controller.CanTransfer)
            return;
        // Default to No so accepting the default button cannot authorize source removal.
        bool confirmed = !move || MessageBox.Show(this,
            "Moving permanently removes original files from the source folder after their destination copies are verified. " +
            "Originals flagged as duplicates stay in place. Continue with Move photos?",
            "Confirm move", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
            return;

        await Run("Checking destination…",
            (token, progress) => controller.TransferAsync(move, confirmed, token, progress),
            result =>
            {
                logPath = result.LogPath;
                string outcome = result.Cancelled ? "Cancelled" : "Finished";
                string transferred = move ? $"{result.Moved:N0} moved" : $"{result.Copied:N0} copied";
                SetStatus($"{outcome} · {transferred} · {result.Skipped:N0} skipped · {result.Errors:N0} errors");
            });
    }

    private async Task Run<T>(string initial, Func<CancellationToken, IProgress<OperationProgress>, Task<T>> work, Action<T> complete)
    {
        using var cancellation = new CancellationTokenSource();
        active = cancellation;
        UpdateButtons();
        SetStatus(initial);
        ProgressPanel.Visibility = Visibility.Visible;
        ShowProgress(new(initial));

        // Sample the latest update on the UI thread so large folders cannot queue thousands of redraws.
        var progress = new LatestProgress();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            if (!cancellation.IsCancellationRequested && progress.Take() is { } update)
                ShowProgress(update);
        };
        timer.Start();

        try
        {
            var result = await work(cancellation.Token, progress);
            complete(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus("Preview cancelled.");
        }
        catch (Exception error)
        {
            SetStatus(error.Message);
        }
        finally
        {
            timer.Stop();
            ProgressPanel.Visibility = Visibility.Collapsed;
            OperationBar.IsIndeterminate = false;
            active = null;
            UpdateButtons();
        }
    }

    private void ShowProgress(OperationProgress update)
    {
        OperationBar.IsIndeterminate = !update.Total.HasValue;
        OperationBar.Maximum = Math.Max(1, update.Total ?? 1);
        OperationBar.Value = update.Completed;
        Status.Text = update.Total is int total
            ? $"{update.Phase} · {update.Completed:N0} of {total:N0} finished"
            : update.Phase;
        CurrentFile.Text = update.Path ?? "";
        CurrentFile.ToolTip = update.Path;
    }

    private void ShowAbout(object sender, RoutedEventArgs e)
    {
        // Read the version shipped with the executable so About follows the build version.
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        MessageBox.Show(this,
            $"Photo Organiser v{version}\n\n" +
            "Organises photos into year and month folders using capture dates when available. " +
            "You can copy photos or confirm a move that removes the transferred originals.\n\n" +
            "Requires the .NET 10 Desktop Runtime (Windows x64 for the packaged app).\n" +
            $"Running on .NET {Environment.Version}.",
            "About Photo Organiser", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetStatus(string message)
    {
        Status.Text = message;
        UIElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // Block folder changes and overlapping operations while the worker uses the current plan.
    private void UpdateButtons()
    {
        var idle = active is null;
        SourceBox.IsEnabled = DestinationBox.IsEnabled = SourceBrowse.IsEnabled = DestinationBrowse.IsEnabled = idle;
        IncludeSubfolders.IsEnabled = idle;
        PreviewButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(SourceBox.Text)
            && !string.IsNullOrWhiteSpace(DestinationBox.Text);
        CopyButton.IsEnabled = MoveButton.IsEnabled = idle && controller.CanTransfer;
        CancelButton.IsEnabled = idle == false && active?.IsCancellationRequested == false;
        LogButton.IsEnabled = idle && logPath is not null;
    }

    private void Cancel(object? sender, RoutedEventArgs? e)
    {
        active?.Cancel();
        SetStatus("Cancelling…");
        UpdateButtons();
    }

    private void OpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{logPath}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            SetStatus($"Log saved at {logPath}. {error.Message}");
        }
    }

    // Allow the worker to clean up and write its final log entry before the window can close.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (active is not null)
        {
            Cancel(null, null);
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    // Exchange the latest immutable snapshot without blocking the worker or the UI thread.
    private sealed class LatestProgress : IProgress<OperationProgress>
    {
        private OperationProgress? latest;

        public void Report(OperationProgress value) => Interlocked.Exchange(ref latest, value);
        public OperationProgress? Take() => Interlocked.Exchange(ref latest, null);
    }
}
