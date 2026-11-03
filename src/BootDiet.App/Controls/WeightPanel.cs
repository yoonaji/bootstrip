using Avalonia;
using Avalonia.Controls;
using BootDiet.App.ViewModels;

namespace BootDiet.App.Controls;

/// <summary>
/// 가로로 나란히 놓되, 각 칸의 너비를 데이터의 Weight(IWeighted) 비율로 나눈다. 요약 화면의 판정 막대용.
/// 칸 사이는 Gap만큼 띄운다.
/// </summary>
public sealed class WeightPanel : Panel
{
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<WeightPanel, double>(nameof(Gap), 4);

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    static WeightPanel() => AffectsMeasure<WeightPanel>(GapProperty);

    private static double WeightOf(Control c) => (c.DataContext as IWeighted)?.Weight ?? 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var total = visible.Sum(WeightOf);
        var room = Math.Max(0, finalSize.Width - Gap * Math.Max(0, visible.Count - 1));
        var x = 0.0;
        foreach (var child in visible)
        {
            var w = total <= 0 ? 0 : room * WeightOf(child) / total;
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w + Gap;
        }
        return finalSize;
    }
}
