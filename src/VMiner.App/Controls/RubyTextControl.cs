using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VMiner.Models;

namespace VMiner.Controls;

public sealed class RubyTextControl : FrameworkElement
{
    private const double PaddingSize = 14;
    private IReadOnlyList<RubySegment> _segments = [];

    public double BaseFontSize { get; set; } = 22;

    public void SetSegments(IReadOnlyList<RubySegment> segments)
    {
        _segments = segments;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 720 : availableSize.Width;
        return new Size(width, Math.Min(MeasureHeight(width), 320));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(ActualWidth - 2 * PaddingSize, 80);
        var rubySize = Math.Max(9, BaseFontSize * .5);
        var lineHeight = BaseFontSize * 1.45 + rubySize + 6;
        var x = PaddingSize;
        var y = PaddingSize;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var cell in Cells())
        {
            if (cell.Text == "\n")
            {
                x = PaddingSize;
                y += lineHeight;
                continue;
            }

            var baseBrush = Application.Current.TryFindResource("ForegroundBrush") as Brush
                            ?? Brushes.White;
            var readingBrush = Application.Current.TryFindResource("ReadingBrush") as Brush
                               ?? Brushes.SandyBrown;
            var baseText = Format(cell.Text, BaseFontSize, baseBrush, pixelsPerDip);
            var rubyText = Format(cell.Reading, rubySize,
                readingBrush, pixelsPerDip);
            var cellWidth = Math.Max(baseText.WidthIncludingTrailingWhitespace,
                rubyText.WidthIncludingTrailingWhitespace);

            if (x > PaddingSize && x + cellWidth > PaddingSize + width)
            {
                x = PaddingSize;
                y += lineHeight;
            }

            if (!string.IsNullOrEmpty(cell.Reading))
                drawingContext.DrawText(rubyText,
                    new Point(x + (cellWidth - rubyText.Width) / 2, y));
            drawingContext.DrawText(baseText,
                new Point(x + (cellWidth - baseText.Width) / 2, y + rubySize + 4));
            x += cellWidth;
        }
    }

    private double MeasureHeight(double availableWidth)
    {
        var width = Math.Max(availableWidth - 2 * PaddingSize, 80);
        var rubySize = Math.Max(9, BaseFontSize * .5);
        var lineHeight = BaseFontSize * 1.45 + rubySize + 6;
        var x = 0d;
        var lines = 1;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var cell in Cells())
        {
            if (cell.Text == "\n")
            {
                x = 0;
                lines++;
                continue;
            }
            var baseWidth = Format(cell.Text, BaseFontSize, Brushes.White, pixelsPerDip).Width;
            var rubyWidth = Format(cell.Reading, rubySize, Brushes.White, pixelsPerDip).Width;
            var cellWidth = Math.Max(baseWidth, rubyWidth);
            if (x > 0 && x + cellWidth > width)
            {
                x = 0;
                lines++;
            }
            x += cellWidth;
        }
        return 2 * PaddingSize + lines * lineHeight;
    }

    private IEnumerable<RubySegment> Cells()
    {
        foreach (var segment in _segments)
        {
            if (!string.IsNullOrEmpty(segment.Reading) && !segment.Text.Contains('\n'))
            {
                yield return segment;
                continue;
            }
            foreach (var character in segment.Text)
                yield return new RubySegment(character.ToString());
        }
    }

    private static FormattedText Format(
        string value, double size, Brush brush, double pixelsPerDip) => new(
        value,
        CultureInfo.GetCultureInfo("ja-JP"),
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        pixelsPerDip);
}
