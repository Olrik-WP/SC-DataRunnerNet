using DataRunner.Core.Abstractions;
using DataRunner.Ocr.Matching;
using DataRunner.Ocr.Pipeline;
using Microsoft.Extensions.Logging;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Online;

namespace DataRunner.Ocr;

/// <summary>
/// Lazily downloads + instantiates the heavy <see cref="PaddleOcrPipeline"/>.
/// First call may take 5-30 seconds (model download on cold cache, native init).
/// Subsequent calls return the cached singleton.
///
/// Thread-safe: concurrent <see cref="GetAsync"/> calls share the same initialisation Task.
/// </summary>
public sealed class PaddleOcrPipelineFactory : IOcrPipelineFactory, IAsyncDisposable
{
    private readonly ICatalogProvider _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PaddleOcrPipelineFactory> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PaddleOcrPipeline? _instance;

    public PaddleOcrPipelineFactory(
        ICatalogProvider catalog,
        ILoggerFactory loggerFactory)
    {
        _catalog = catalog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PaddleOcrPipelineFactory>();
    }

    public bool IsReady => _instance is not null;

    public async Task<IOcrPipeline> GetAsync(CancellationToken ct = default)
    {
        if (_instance is { } cached) return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_instance is not null) return _instance;

            // Ensure the catalog is loaded once before we spin up the matcher.
            // RefreshAsync is a no-op when an in-memory snapshot is already there.
            if (_catalog.Commodities.Count == 0)
            {
                _logger.LogInformation("UEX catalog empty; pulling from API before OCR boot.");
                await _catalog.RefreshAsync(force: false, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Downloading / loading PaddleOCR English V4 models...");
            var model = await OnlineFullModels.EnglishV4.DownloadAsync(ct).ConfigureAwait(false);

            var ocr = new PaddleOcrAll(model)
            {
                AllowRotateDetection = false,
                Enable180Classification = false,
            };

            var matcher = new FuzzyMatcher(_catalog);
            var parser = new CommodityParser(matcher);
            _instance = new PaddleOcrPipeline(
                ocr,
                parser,
                matcher,
                _loggerFactory.CreateLogger<PaddleOcrPipeline>());

            _logger.LogInformation("PaddleOCR pipeline ready.");
            return _instance;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _instance?.Dispose();
        _instance = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
