using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DataRunner.App.ViewModels;
using DataRunner.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.Services;

public enum OcrPipelineStatus
{
    Idle,        // No OCR has been requested yet, models not loaded.
    Initializing, // First call: downloading / loading PaddleOCR models.
    Ready,       // Models loaded, no jobs running right now.
    Processing,  // At least one OCR job is running.
    Failed,      // Last init failed; user-visible error.
}

/// <summary>
/// Single entry point for "we got a screenshot, run the pipeline on it":
///  - Used by manual <see cref="InboxViewModel.Import"/>
///  - Used by <see cref="ScreenshotFolderWatcher"/> for auto-import
///  - Used by <see cref="ScreenshotEditViewModel.ReRunOcrCommand"/> to retry
///
/// Tracks pipeline lifetime (Initializing / Ready / Processing) so the UI can
/// surface "models still loading, please wait" without hard-coding any timer.
/// All UI mutations are marshalled back to the dispatcher thread.
/// </summary>
public sealed partial class OcrCoordinator : ObservableObject
{
    private readonly IOcrPipelineFactory _factory;
    private readonly InboxViewModel _inbox;
    private readonly ILogger<OcrCoordinator> _logger;

    private int _activeJobs;

    [ObservableProperty] private OcrPipelineStatus _status = OcrPipelineStatus.Idle;
    [ObservableProperty] private string _statusMessage = "OCR engine idle";
    [ObservableProperty] private int _activeJobsCount;
    [ObservableProperty] private bool _isBusy;

    public OcrCoordinator(
        IOcrPipelineFactory factory,
        InboxViewModel inbox,
        ILogger<OcrCoordinator> logger)
    {
        _factory = factory;
        _inbox = inbox;
        _logger = logger;
    }

    /// <summary>
    /// Queues a new image for OCR. Idempotent: if the same image path is already
    /// in the inbox, returns the existing item instead of duplicating it.
    /// </summary>
    public InboxItem EnqueueAndProcess(string imagePath)
    {
        var existing = _inbox.Items.FirstOrDefault(
            i => string.Equals(i.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var item = new InboxItem
        {
            ImagePath = imagePath,
            DisplayName = Path.GetFileName(imagePath),
            Status = InboxStatus.Processing,
            StatusReason = "Waiting for OCR…",
            AddedAt = DateTimeOffset.Now,
        };
        InvokeOnUi(() => _inbox.Items.Add(item));
        _ = ProcessAsync(item);
        return item;
    }

    /// <summary>
    /// Forces a re-run on an already known item (typically called from the editor
    /// when the user clicks "Re-run OCR" after fixing image quality / picking a
    /// different screenshot of the same terminal).
    /// </summary>
    public void Reprocess(InboxItem item)
    {
        InvokeOnUi(() =>
        {
            item.Submission = null;
            item.Payload = null;
            item.TerminalLabel = null;
            item.RowCount = 0;
            item.Status = InboxStatus.Processing;
            item.StatusReason = "Re-running OCR…";
        });
        _ = ProcessAsync(item);
    }

    private async Task ProcessAsync(InboxItem item)
    {
        var wasFirstInit = !_factory.IsReady;
        if (wasFirstInit)
        {
            UpdateStatus(OcrPipelineStatus.Initializing,
                "Loading OCR models (first launch only, ~10-30s)…");
        }

        Interlocked.Increment(ref _activeJobs);
        InvokeOnUi(() =>
        {
            ActiveJobsCount = _activeJobs;
            IsBusy = true;
            if (!wasFirstInit) UpdateStatusInline();
        });

        try
        {
            _logger.LogInformation("OCR queued: {Path}", item.ImagePath);
            var pipeline = await _factory.GetAsync().ConfigureAwait(false);
            var result = await pipeline.RunAsync(item.ImagePath).ConfigureAwait(false);

            InvokeOnUi(() =>
            {
                item.Submission = result.Submission;
                item.Payload = result.Payload;
                item.TerminalLabel = result.Submission.TerminalDisplayName ?? "(terminal not detected)";
                item.RowCount = result.Submission.Prices.Count;
                item.Status = result.Submission.NeedsReview.Count == 0 && result.Submission.IdTerminal is not null
                    ? InboxStatus.Ready
                    : InboxStatus.Review;
                item.StatusReason = result.Submission.NeedsReview.Count switch
                {
                    0 => "Ready to send",
                    1 => "1 item to review",
                    var n => $"{n} items to review",
                };

                if (ReferenceEquals(_inbox.SelectedItem, item) && _inbox.CurrentEditor is { } editor)
                {
                    editor.Load(item);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR pipeline failed for {Path}", item.ImagePath);
            InvokeOnUi(() =>
            {
                item.Status = InboxStatus.Failed;
                item.StatusReason = ex.Message;
                if (wasFirstInit && !_factory.IsReady)
                {
                    UpdateStatus(OcrPipelineStatus.Failed,
                        $"OCR engine failed to load: {ex.Message}");
                }
            });
        }
        finally
        {
            Interlocked.Decrement(ref _activeJobs);
            InvokeOnUi(() =>
            {
                ActiveJobsCount = _activeJobs;
                IsBusy = _activeJobs > 0;
                UpdateStatusInline();
            });
        }
    }

    private void UpdateStatusInline()
    {
        if (Status == OcrPipelineStatus.Failed) return;

        if (_activeJobs > 0)
        {
            UpdateStatus(OcrPipelineStatus.Processing,
                _activeJobs == 1
                    ? "Running OCR on 1 screenshot…"
                    : $"Running OCR on {_activeJobs} screenshots…");
        }
        else if (_factory.IsReady)
        {
            UpdateStatus(OcrPipelineStatus.Ready, "OCR engine ready");
        }
        else
        {
            UpdateStatus(OcrPipelineStatus.Idle, "OCR engine idle");
        }
    }

    private void UpdateStatus(OcrPipelineStatus status, string message)
    {
        InvokeOnUi(() =>
        {
            Status = status;
            StatusMessage = message;
        });
    }

    private static void InvokeOnUi(Action a)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) a();
        else dispatcher.Invoke(a);
    }
}
