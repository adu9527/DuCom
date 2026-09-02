using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DuCom.Core.Parsing;

namespace DuCom.Controls;

/// <summary>
/// Renders Core <see cref="StyleRun"/> sequences as the Inlines of a single TextBlock so
/// word wrap, invisible-character substitution, and per-run styling compose correctly.
/// Brushes are cached and frozen (render resource pooling guardrail). This control only
/// reads display data; it never touches sessions or the receive pipeline.
/// </summary>
public sealed class StyledRunsTextBlock : TextBlock
{
    private static readonly ConcurrentDictionary<int, SolidColorBrush> BrushCache = new();

    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(
        nameof(Runs),
        typeof(IReadOnlyList<StyleRun>),
        typeof(StyledRunsTextBlock),
        new PropertyMetadata(null, OnRenderStateChanged));

    public static readonly DependencyProperty ShowControlCharactersProperty = DependencyProperty.Register(
        nameof(ShowControlCharacters),
        typeof(bool),
        typeof(StyledRunsTextBlock),
        new PropertyMetadata(false, OnRenderStateChanged));

    public static readonly DependencyProperty ShowSpacesProperty = DependencyProperty.Register(
        nameof(ShowSpaces),
        typeof(bool),
        typeof(StyledRunsTextBlock),
        new PropertyMetadata(false, OnRenderStateChanged));

    public static readonly DependencyProperty ShowTabsProperty = DependencyProperty.Register(
        nameof(ShowTabs),
        typeof(bool),
        typeof(StyledRunsTextBlock),
        new PropertyMetadata(false, OnRenderStateChanged));

    public IReadOnlyList<StyleRun>? Runs
    {
        get => (IReadOnlyList<StyleRun>?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    public bool ShowControlCharacters
    {
        get => (bool)GetValue(ShowControlCharactersProperty);
        set => SetValue(ShowControlCharactersProperty, value);
    }

    public bool ShowSpaces
    {
        get => (bool)GetValue(ShowSpacesProperty);
        set => SetValue(ShowSpacesProperty, value);
    }

    public bool ShowTabs
    {
        get => (bool)GetValue(ShowTabsProperty);
        set => SetValue(ShowTabsProperty, value);
    }

    private static void OnRenderStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StyledRunsTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        if (Runs is null || Runs.Count == 0)
        {
            return;
        }

        // Resolve theme-aware defaults up-front so any run lacking explicit
        // colours still renders as readable text on the log surface (this
        // matches the guarantees BoundedLogEditor provides for the main log view).
        Brush defaultForeground = (Brush?)TryFindResource("Brush.LogText") ?? Foreground ?? Brushes.Gainsboro;
        Brush defaultBackground = (Brush?)TryFindResource("Brush.LogSurface") ?? Brushes.Transparent;

        foreach (StyleRun run in Runs)
        {
            string text = DisplayTextTransform.Apply(run.Text, ShowControlCharacters, ShowSpaces, ShowTabs);
            if (text.Length == 0)
            {
                continue;
            }

            Run inline = new() { Text = text };
            if (run.HasForeground)
            {
                inline.Foreground = GetBrush(run.ForegroundR!.Value, run.ForegroundG!.Value, run.ForegroundB!.Value);
            }
            else if (run.Inverse)
            {
                inline.Foreground = defaultBackground;
            }
            else
            {
                inline.Foreground = defaultForeground;
            }

            if (run.HasBackground)
            {
                inline.Background = GetBrush(run.BackgroundR!.Value, run.BackgroundG!.Value, run.BackgroundB!.Value);
            }
            else if (run.Inverse)
            {
                inline.Background = defaultForeground;
            }

            if (run.Bold)
            {
                inline.FontWeight = FontWeights.Bold;
            }

            if (run.Italic)
            {
                inline.FontStyle = FontStyles.Italic;
            }

            if (run.Underline)
            {
                inline.TextDecorations = System.Windows.TextDecorations.Underline;
            }

            Inlines.Add(inline);
        }
    }

    private static SolidColorBrush GetBrush(byte r, byte g, byte b)
    {
        int key = (r << 16) | (g << 8) | b;
        return BrushCache.GetOrAdd(key, static (_, rgb) =>
        {
            SolidColorBrush brush = new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }, key);
    }
}
