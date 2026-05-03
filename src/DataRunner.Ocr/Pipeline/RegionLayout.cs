using System.Text;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace DataRunner.Ocr.Pipeline;

/// <summary>
/// Reconstructs OCR text from <see cref="PaddleOcrResultRegion"/> arrays using the
/// 2D layout instead of Paddle's default Y-then-X linearization (which simply
/// joins every region with '\n').
///
/// Why we need this: PaddleOCR's detector returns one region per text bounding box.
/// On Star Citizen commodity terminals, "3000 SCU" is often emitted as TWO regions
/// ("3000" and "SCU"), placed side by side on the same visual row. The default
/// PaddleOcrResult.Text joins them with '\n', which then breaks the
/// ScuRegex / PriceRegex in CommodityParser (regex don't span lines).
///
/// What this helper does:
///   1. Group regions into VISUAL ROWS by clustering on Y centers.
///   2. Within each row, sort by X and merge horizontally-adjacent regions whose
///      gap is small enough to be a single logical token (eg. "3000" + "SCU"
///      -> "3000 SCU") into one output line.
///   3. Emit one output line per merged group, preserving row order then X order.
///
/// Result: the parser still sees one info chunk per line (commodity name | SCU |
/// price | status), but with fragments joined back together.
///
/// Compatibility: pure CPU, OpenCvSharp only. No GPU dependency.
/// </summary>
public static class RegionLayout
{
    /// <summary>
    /// Multiplier on the median region height used to decide whether two regions
    /// belong to the same visual row. 0.6 means: regions whose Y centers differ by
    /// less than 60% of the typical row height are considered same-row.
    /// </summary>
    private const double RowToleranceFactor = 0.6;

    /// <summary>
    /// Multiplier on the median region height used to decide whether two regions
    /// in the same row should be merged into a single token. 1.2 means: regions
    /// less than ~1.2 char-widths apart get joined with a single space.
    /// </summary>
    private const double HorizontalMergeFactor = 1.2;

    /// <summary>
    /// Reconstructs the OCR text using row clustering + intra-row horizontal merging.
    /// Returns one logical token per output line.
    /// </summary>
    public static string JoinByRows(IReadOnlyList<PaddleOcrResultRegion> regions)
    {
        if (regions is null || regions.Count == 0) return string.Empty;

        var items = new List<RegionItem>(regions.Count);
        foreach (var r in regions)
        {
            var text = r.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            var bounds = r.Rect.BoundingRect();
            items.Add(new RegionItem(
                Text: text,
                CenterX: r.Rect.Center.X,
                CenterY: r.Rect.Center.Y,
                Left: bounds.Left,
                Right: bounds.Right,
                Height: bounds.Height));
        }

        if (items.Count == 0) return string.Empty;

        items.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));

        // Use median region height to derive tolerances. Median is robust to outliers
        // (a single tall character or a stray icon won't blow up the threshold).
        var sortedHeights = items.Select(i => i.Height).OrderBy(h => h).ToArray();
        var medianHeight = sortedHeights[sortedHeights.Length / 2];
        if (medianHeight < 8) medianHeight = 8;

        var rowTolerance = medianHeight * RowToleranceFactor;
        var horizontalGap = medianHeight * HorizontalMergeFactor;

        var output = new StringBuilder();
        var currentRow = new List<RegionItem>();
        double rowAvgY = 0.0;

        foreach (var item in items)
        {
            if (currentRow.Count == 0)
            {
                currentRow.Add(item);
                rowAvgY = item.CenterY;
                continue;
            }

            if (Math.Abs(item.CenterY - rowAvgY) <= rowTolerance)
            {
                currentRow.Add(item);
                rowAvgY = currentRow.Average(r => r.CenterY);
            }
            else
            {
                FlushRow(currentRow, horizontalGap, output);
                currentRow.Clear();
                currentRow.Add(item);
                rowAvgY = item.CenterY;
            }
        }
        FlushRow(currentRow, horizontalGap, output);

        return output.ToString();
    }

    private static void FlushRow(List<RegionItem> row, double horizontalGap, StringBuilder output)
    {
        if (row.Count == 0) return;

        row.Sort((a, b) => a.CenterX.CompareTo(b.CenterX));

        var pending = new StringBuilder();
        var pendingRight = double.MinValue;

        foreach (var item in row)
        {
            if (pending.Length == 0)
            {
                pending.Append(item.Text);
                pendingRight = item.Right;
                continue;
            }

            var gap = item.Left - pendingRight;
            if (gap <= horizontalGap)
            {
                pending.Append(' ').Append(item.Text);
                pendingRight = item.Right;
            }
            else
            {
                output.AppendLine(pending.ToString());
                pending.Clear();
                pending.Append(item.Text);
                pendingRight = item.Right;
            }
        }

        if (pending.Length > 0)
        {
            output.AppendLine(pending.ToString());
        }
    }

    private readonly record struct RegionItem(
        string Text,
        double CenterX,
        double CenterY,
        int Left,
        int Right,
        int Height);
}
