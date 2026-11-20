namespace PhotoOrganiser;

// Owns the reviewed plan and the rules for starting a transfer. The WPF view supplies
// user input and confirmation, while Organiser performs the filesystem operations.
public sealed class PhotoController(Organiser organiser)
{
    private Preview? preview;
    public bool CanTransfer { get; private set; }

    public void ClearPreview()
    {
        preview = null;
        CanTransfer = false;
    }

    public Task<Preview> BuildPreviewAsync(string source, string destination, bool includeSubfolders,
        CancellationToken token, IProgress<OperationProgress> progress) =>
        Task.Run(() => organiser.Scan(source, destination, token, progress, includeSubfolders));

    public void Review(Preview plan)
    {
        preview = plan;
        CanTransfer = plan.Entries.Any(row => row.Action == "Copy");
    }

    public Task<CopyResult> TransferAsync(bool move, bool confirmed, CancellationToken token,
        IProgress<OperationProgress> progress)
    {
        if (!CanTransfer || preview is not Preview plan || (move && !confirmed))
            throw new InvalidOperationException("A transfer requires a current preview and confirmation for moves.");

        // Consume the preview before starting the worker so each reviewed plan is used once.
        ClearPreview();
        return Task.Run(() => move ? organiser.Move(plan, token, progress) : organiser.Copy(plan, token, progress));
    }
}
