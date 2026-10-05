using System.Windows;
using System.Windows.Controls;

namespace Classify.UI;

/// <summary>带间距的 StackPanel（WPF 无 UWP 的 Spacing 属性，这里等价实现）。</summary>
public class SpacedPanel : StackPanel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(SpacedPanel), new PropertyMetadata(0.0));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        ApplyGap();
        return base.MeasureOverride(constraint);
    }

    private void ApplyGap()
    {
        var vertical = Orientation == Orientation.Vertical;
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not FrameworkElement el) continue;
            var target = i == Children.Count - 1
                ? new Thickness(0)
                : (vertical ? new Thickness(0, 0, 0, Gap) : new Thickness(0, 0, Gap, 0));
            if (el.Margin != target) el.Margin = target;
        }
    }
}
