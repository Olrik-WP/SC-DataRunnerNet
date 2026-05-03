using Spectre.Console;

namespace DataRunner.OcrSpike;

public static class TessdataBootstrap
{
    private const string TessdataFastBaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main";

    public static async Task EnsureLanguageAsync(string tessdataDir, string lang, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tessdataDir);
        var target = Path.Combine(tessdataDir, $"{lang}.traineddata");

        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            return;
        }

        var url = $"{TessdataFastBaseUrl}/{lang}.traineddata";
        AnsiConsole.MarkupLine($"[yellow]Downloading {lang}.traineddata from tessdata_fast...[/]");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct);

        AnsiConsole.MarkupLine($"[green]Saved {target} ({new FileInfo(target).Length / 1024} KB)[/]");
    }
}
