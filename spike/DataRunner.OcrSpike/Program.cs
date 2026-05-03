using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using DataRunner.Ocr;
using DataRunner.OcrSpike;
using DataRunner.OcrSpike.Bench;
using DataRunner.OcrSpike.Engines;
using DataRunner.OcrSpike.Metrics;
using DataRunner.OcrSpike.Models;
using DataRunner.OcrSpike.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

var repoRoot = LocateRepoRoot();
var screenshotsDir = Path.Combine(repoRoot, "samples", "screenshots");
var groundTruthDir = Path.Combine(repoRoot, "samples", "ground_truth");
var uexCacheDir = Path.Combine(repoRoot, "samples", "uex_cache");
var resultsDir = Path.Combine(repoRoot, "results");
var tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");

Directory.CreateDirectory(resultsDir);

if (!Directory.Exists(screenshotsDir))
{
    AnsiConsole.MarkupLine($"[red]Screenshots folder not found: {screenshotsDir}[/]");
    return 1;
}

var imagePaths = Directory
    .EnumerateFiles(screenshotsDir, "*.*", SearchOption.TopDirectoryOnly)
    .Where(p => p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
             || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
             || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
             || p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
    .OrderBy(p => p)
    .ToList();

if (imagePaths.Count == 0)
{
    AnsiConsole.MarkupLine($"[yellow]No screenshots found in {screenshotsDir}.[/]");
    return 0;
}

AnsiConsole.MarkupLine($"[green]Found {imagePaths.Count} screenshot(s).[/]");

await TessdataBootstrap.EnsureLanguageAsync(tessdataDir, "eng");

ICatalogProvider catalog;
try
{
    catalog = DiskCatalogProvider.LoadFromCache(uexCacheDir);
    AnsiConsole.MarkupLine(
        $"[green]UEX catalog loaded: {catalog.Commodities.Count} commodities, "
        + $"{catalog.CommodityTerminals.Count} commodity terminals.[/]");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Failed to load UEX cache: {ex.Message}[/]");
    return 1;
}

var benchmarkRows = new List<BenchmarkRow>();
var pipelineRuns = new List<(string Image, OcrPipelineResult Result)>();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Initializing OCR engines...", async ctx =>
    {
        ctx.Status("Loading Tesseract...");
        using var tesseract = new TesseractEngine(tessdataDir, "eng");

        ctx.Status("Loading PaddleOCR base...");
        using var paddleBase = await PaddleOcrEngine.CreateAsync();

        ctx.Status("Loading PaddleOCR pipeline (preproc + parser + matcher)...");
        var pipelineFactory = new PaddleOcrPipelineFactory(catalog, NullLoggerFactory.Instance);
        var pipeline = await pipelineFactory.GetAsync();

        IOcrEngine[] basicEngines = [tesseract, paddleBase];

        foreach (var imagePath in imagePaths)
        {
            var imageName = Path.GetFileName(imagePath);
            var gt = TryLoadGroundTruth(groundTruthDir, imageName);

            foreach (var engine in basicEngines)
            {
                ctx.Status($"[cyan]{engine.Name}[/] on [yellow]{imageName}[/]...");
                var result = await engine.RecognizeAsync(imagePath);

                var cer = gt is null ? double.NaN : ErrorRate.Cer(gt.ExpectedText, result.Text);
                var wer = gt is null ? double.NaN : ErrorRate.Wer(gt.ExpectedText, result.Text);

                benchmarkRows.Add(new BenchmarkRow(
                    Image: imageName,
                    Engine: engine.Name,
                    Cer: cer,
                    Wer: wer,
                    MeanConfidence: result.MeanConfidence,
                    ElapsedMs: result.ElapsedMs,
                    RawText: result.Text));
            }

            ctx.Status($"[cyan]Pipeline[/] on [yellow]{imageName}[/]...");
            var pipelineResult = await pipeline.RunAsync(imagePath);

            var pCer = gt is null ? double.NaN : ErrorRate.Cer(gt.ExpectedText, pipelineResult.Ocr.Text);
            var pWer = gt is null ? double.NaN : ErrorRate.Wer(gt.ExpectedText, pipelineResult.Ocr.Text);

            benchmarkRows.Add(new BenchmarkRow(
                Image: imageName,
                Engine: pipelineResult.Ocr.EngineName,
                Cer: pCer,
                Wer: pWer,
                MeanConfidence: pipelineResult.Ocr.MeanConfidence,
                ElapsedMs: pipelineResult.Ocr.ElapsedMs,
                RawText: pipelineResult.Ocr.Text));

            pipelineRuns.Add((imageName, pipelineResult));

            var nameNoExt = Path.GetFileNameWithoutExtension(imageName);

            var submissionPath = Path.Combine(resultsDir, $"{nameNoExt}.submission.json");
            await File.WriteAllTextAsync(submissionPath, JsonSerializer.Serialize(
                pipelineResult.Submission,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                }));

            var payloadPath = Path.Combine(resultsDir, $"{nameNoExt}.uex_payload.json");
            await File.WriteAllTextAsync(payloadPath, JsonSerializer.Serialize(
                pipelineResult.Payload,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                }));
        }
    });

PrintConsoleTable(benchmarkRows);
PrintPipelineSummary(pipelineRuns);

var reportPath = Path.Combine(resultsDir, "benchmark.md");
await File.WriteAllTextAsync(reportPath, MarkdownReport.Build(benchmarkRows, pipelineRuns));
AnsiConsole.MarkupLine($"\n[green]Report written:[/] {reportPath}");

return 0;

static string LocateRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SC-DataRunnerNet.sln")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName
        ?? throw new InvalidOperationException("Could not locate repo root (SC-DataRunnerNet.sln).");
}

static GroundTruth? TryLoadGroundTruth(string groundTruthDir, string imageName)
{
    var nameNoExt = Path.GetFileNameWithoutExtension(imageName);
    var gtPath = Path.Combine(groundTruthDir, $"{nameNoExt}.json");
    if (!File.Exists(gtPath)) return null;
    var json = File.ReadAllText(gtPath);
    return JsonSerializer.Deserialize<GroundTruth>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    });
}

static void PrintConsoleTable(IReadOnlyList<BenchmarkRow> rows)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("Image")
        .AddColumn("Engine")
        .AddColumn(new TableColumn("CER").RightAligned())
        .AddColumn(new TableColumn("WER").RightAligned())
        .AddColumn(new TableColumn("Conf.").RightAligned())
        .AddColumn(new TableColumn("Time (ms)").RightAligned());

    foreach (var r in rows)
    {
        var cer = double.IsNaN(r.Cer) ? "n/a" : r.Cer.ToString("F3");
        var wer = double.IsNaN(r.Wer) ? "n/a" : r.Wer.ToString("F3");
        table.AddRow(r.Image, r.Engine, cer, wer, r.MeanConfidence.ToString("F2"), r.ElapsedMs.ToString());
    }

    AnsiConsole.Write(table);
}

static void PrintPipelineSummary(IReadOnlyList<(string Image, OcrPipelineResult Result)> runs)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold underline]Pipeline structured output[/]  [dim](DRAFT - human review required before submit)[/]");

    foreach (var (image, run) in runs)
    {
        var sub = run.Submission;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(image)}[/]");

        var terminalLine = sub.IdTerminal is null
            ? "[red]Terminal: not detected[/]"
            : $"[green]Terminal:[/] [white]{Markup.Escape(sub.TerminalDisplayName ?? "")}[/] (id={sub.IdTerminal}, "
              + $"score={sub.TerminalMatchScore:F0}, field={Markup.Escape(sub.TerminalMatchedField ?? "")}, "
              + $"from='{Markup.Escape(sub.TerminalMatchedFromOcr ?? "")}')";
        AnsiConsole.MarkupLine($"  {terminalLine}");
        AnsiConsole.MarkupLine($"  [grey]Tab:[/] {sub.Tab}    [grey]Container sizes:[/] {Markup.Escape(sub.ContainerSizes ?? "-")}");

        if (sub.Prices.Count == 0)
        {
            AnsiConsole.MarkupLine("  [red]No commodity rows parsed.[/]");
        }
        else
        {
            var t = new Table().Border(TableBorder.Minimal)
                .AddColumn("Commodity (matched)")
                .AddColumn(new TableColumn("Score").RightAligned())
                .AddColumn(new TableColumn("SCU").RightAligned())
                .AddColumn(new TableColumn("Price").RightAligned())
                .AddColumn(new TableColumn("Status").RightAligned())
                .AddColumn("From OCR (commodity)");

            foreach (var p in sub.Prices)
            {
                t.AddRow(
                    Markup.Escape(p.CommodityName ?? "?"),
                    p.CommodityMatchScore.ToString("F0"),
                    p.ScuBuy?.ToString() ?? "-",
                    p.PriceBuy?.ToString("F2") ?? "-",
                    p.StatusBuy == InventoryStatus.Unknown ? "?" : $"{(int)p.StatusBuy}({p.StatusBuy})",
                    Markup.Escape(p.CommodityMatchedFromOcr ?? "?"));
            }

            AnsiConsole.Write(t);
        }

        if (sub.NeedsReview.Count > 0)
        {
            AnsiConsole.MarkupLine("  [yellow]Needs review:[/]");
            foreach (var nr in sub.NeedsReview)
            {
                AnsiConsole.MarkupLine($"    - {Markup.Escape(nr)}");
            }
        }
    }
}
