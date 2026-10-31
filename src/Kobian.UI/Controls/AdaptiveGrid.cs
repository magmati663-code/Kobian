using Avalonia;
using Avalonia.Controls;

namespace Kobian.UI.Controls;

/// <summary>
/// Siatka kafelków o równych szerokościach: liczba kolumn = ⌊(szerokość + odstęp) / (MinItemWidth + odstęp)⌋ (min. 1, maks. MaxColumns),
/// wiersz ma wysokość najwyższego kafelka. Wypełnia całą szerokość przy 100–200% skali (6.9) zamiast zostawiać pustą kolumnę jak WrapPanel.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(MinItemWidth), 320);
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(Spacing), 16);
    public static readonly StyledProperty<int> MaxColumnsProperty = AvaloniaProperty.Register<AdaptiveGrid, int>(nameof(MaxColumns), 3);

    static AdaptiveGrid() => AffectsMeasure<AdaptiveGrid>(MinItemWidthProperty, SpacingProperty, MaxColumnsProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public int MaxColumns { get => GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    internal static int Columns(double width, double min, double spacing, int max, int count)
    {
        if (count == 0) return 1;
        var cols = double.IsInfinity(width) ? max : (int)Math.Floor((width + spacing) / (min + spacing));
        return Math.Clamp(Math.Min(cols, count), 1, Math.Max(1, max));
    }

    private double _itemWidth;
    private int _cols = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        _cols = Columns(availableSize.Width, MinItemWidth, Spacing, MaxColumns, count);
        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth * _cols + Spacing * (_cols - 1) : availableSize.Width;
        _itemWidth = Math.Max(0, (width - Spacing * (_cols - 1)) / _cols);
        double total = 0, rowH = 0;
        for (var i = 0; i < count; i++)
        {
            var c = Children[i];
            c.Measure(new Size(_itemWidth, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if ((i + 1) % _cols == 0 || i == count - 1)
            {
                total += rowH + (i == count - 1 ? 0 : Spacing);
                rowH = 0;
            }
        }
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        double y = 0;
        for (var start = 0; start < count; start += _cols)
        {
            double rowH = 0;
            for (var i = start; i < Math.Min(start + _cols, count); i++) rowH = Math.Max(rowH, Children[i].DesiredSize.Height);
            for (var i = start; i < Math.Min(start + _cols, count); i++)
                Children[i].Arrange(new Rect((i - start) * (_itemWidth + Spacing), y, _itemWidth, rowH));
            y += rowH + Spacing;
        }
        return finalSize;
    }
}
