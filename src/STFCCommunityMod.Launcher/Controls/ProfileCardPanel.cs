using System.Windows;
using System.Windows.Controls;

namespace STFCCommunityMod.Launcher.Controls;

/// <summary>Equal-width profile cards with room for wrapped names and recovery actions.</summary>
public sealed class ProfileCardPanel : Panel
{
    private const double Gap = 18;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1060;
        var columns = ColumnCount(width);
        var cardWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var height = 0d;
        for (var start = 0; start < InternalChildren.Count; start += columns)
        {
            var rowHeight = 0d;
            for (var index = start; index < Math.Min(start + columns, InternalChildren.Count); index++)
            {
                var child = InternalChildren[index];
                child.Measure(new Size(cardWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (start == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width);
        var cardWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var top = 0d;
        for (var start = 0; start < InternalChildren.Count; start += columns)
        {
            var rowHeight = 0d;
            for (var index = start; index < Math.Min(start + columns, InternalChildren.Count); index++)
                rowHeight = Math.Max(rowHeight, InternalChildren[index].DesiredSize.Height);
            for (var index = start; index < Math.Min(start + columns, InternalChildren.Count); index++)
                InternalChildren[index].Arrange(new Rect((index - start) * (cardWidth + Gap), top, cardWidth, rowHeight));
            top += rowHeight + Gap;
        }
        return finalSize;
    }

    private static int ColumnCount(double width) => width <= 640 ? 1 : width <= 820 ? 2 : 3;
}
