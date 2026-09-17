using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using DuCom.Core.Parsing;
using DuCom.ViewModels;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DuCom.Controls;

public sealed partial class BoundedLogEditor
{
    private static readonly ConcurrentDictionary<int, SolidColorBrush> BrushCache = new();
    private readonly LogColorizer _colorizer = new();
    private readonly List<ColorSpan> _spans = [];
    private long _documentOriginOffset;

    private static bool Matches(ProjectedLine projected, LogLineViewModel line) =>
        ReferenceEquals(projected.Source, line) ||
        projected.Source.LogicalId == line.LogicalId &&
        projected.Source.SegmentIndex == line.SegmentIndex &&
        string.Equals(projected.Source.Text, line.Text, StringComparison.Ordinal) &&
        projected.Source.StyledRuns.SequenceEqual(line.StyledRuns);

    private sealed record ProjectedLine(
        LogLineViewModel Source,
        long StartOffset,
        long EndOffset)
    {
        public long LogicalId => Source.LogicalId;
        public int SegmentIndex => Source.SegmentIndex;
        public string Text => Source.Text;
        public IReadOnlyList<StyleRun> StyledRuns => Source.StyledRuns;
    }

    private sealed record ViewportAnchor(long LogicalId, int SegmentIndex, double OffsetWithinLine);

    private readonly record struct ColorSpan(long Offset, int Length, StyleRun Style);

    private sealed class LogColorizer : DocumentColorizingTransformer
    {
        private IReadOnlyList<ColorSpan> _spans = [];
        private long _originOffset;

        public void SetSpans(IReadOnlyList<ColorSpan> spans, long originOffset = 0)
        {
            _spans = spans;
            _originOffset = originOffset;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            int lineEnd = line.EndOffset;
            int index = LowerBound(line.Offset);
            for (; index < _spans.Count; index++)
            {
                ColorSpan span = _spans[index];
                long spanOffset = span.Offset - _originOffset;
                if (spanOffset >= lineEnd)
                {
                    break;
                }
                int start = (int)Math.Max(line.Offset, spanOffset);
                int end = (int)Math.Min(lineEnd, spanOffset + span.Length);
                if (start < end)
                {
                    ChangeLinePart(start, end, element => ApplyStyle(element, span.Style));
                }
            }
        }

        private int LowerBound(int offset)
        {
            int low = 0;
            int high = _spans.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_spans[middle].Offset - _originOffset + _spans[middle].Length <= offset)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        private static void ApplyStyle(VisualLineElement element, StyleRun style)
        {
            // Resolve theme-aware defaults from the WPF resource dictionary so a run
            // without explicit colors still reads as "text on log surface" rather
            // than the hard-coded Brushes.Black/White (which would vanish on the
            // matching background in either theme).
            Brush defaultForeground = (Brush?)(Application.Current?.TryFindResource("Brush.LogText"))
                ?? Brushes.Gainsboro;
            Brush defaultBackground = (Brush?)(Application.Current?.TryFindResource("Brush.LogSurface"))
                ?? Brushes.Transparent;

            if (style.Inverse)
            {
                // ANSI inverse: foreground and background swap. When the run only
                // declares one of the two, fall back to the theme's surface/text
                // pairing so the swapped colours stay visible.
                Brush foreground = style.HasBackground
                    ? GetBrush(style.BackgroundR!.Value, style.BackgroundG!.Value, style.BackgroundB!.Value)
                    : defaultBackground;
                Brush background = style.HasForeground
                    ? GetBrush(style.ForegroundR!.Value, style.ForegroundG!.Value, style.ForegroundB!.Value)
                    : defaultForeground;
                element.TextRunProperties.SetForegroundBrush(foreground);
                element.BackgroundBrush = background;
            }
            else
            {
                if (style.HasForeground)
                {
                    element.TextRunProperties.SetForegroundBrush(GetBrush(style.ForegroundR!.Value, style.ForegroundG!.Value, style.ForegroundB!.Value));
                }
                else
                {
                    element.TextRunProperties.SetForegroundBrush(defaultForeground);
                }
                if (style.HasBackground)
                {
                    element.BackgroundBrush = GetBrush(style.BackgroundR!.Value, style.BackgroundG!.Value, style.BackgroundB!.Value);
                }
            }
            if (style.Bold)
            {
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, element.TextRunProperties.Typeface.Style, FontWeights.Bold, element.TextRunProperties.Typeface.Stretch));
            }
            if (style.Italic)
            {
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyles.Italic, element.TextRunProperties.Typeface.Weight, element.TextRunProperties.Typeface.Stretch));
            }
            if (style.Underline)
            {
                element.TextRunProperties.SetTextDecorations(System.Windows.TextDecorations.Underline);
            }
        }

        private static SolidColorBrush GetBrush(byte r, byte g, byte b)
        {
            int key = (r << 16) | (g << 8) | b;
            return BrushCache.GetOrAdd(key, static value =>
            {
                SolidColorBrush brush = new(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value));
                brush.Freeze();
                return brush;
            });
        }
    }
}
