using System.Windows;
using System.Windows.Controls;

namespace TaskbarMonitor.UI;

/// <summary>Small bounded grid that keeps sparse widgets vertically centered and dense widgets in at most two rows.</summary>
public sealed class AdaptiveUniformPanel : Panel
{
    public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(
        nameof(MaxRows), typeof(int), typeof(AdaptiveUniformPanel), new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MinimumCellWidthProperty = DependencyProperty.Register(
        nameof(MinimumCellWidth), typeof(double), typeof(AdaptiveUniformPanel), new FrameworkPropertyMetadata(96d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty HorizontalGapProperty = DependencyProperty.Register(
        nameof(HorizontalGap), typeof(double), typeof(AdaptiveUniformPanel), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty VerticalGapProperty = DependencyProperty.Register(
        nameof(VerticalGap), typeof(double), typeof(AdaptiveUniformPanel), new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty PreferredSecondRowKeyProperty = DependencyProperty.Register(
        nameof(PreferredSecondRowKey), typeof(string), typeof(AdaptiveUniformPanel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int MaxRows { get => (int)GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }
    public double MinimumCellWidth { get => (double)GetValue(MinimumCellWidthProperty); set => SetValue(MinimumCellWidthProperty, value); }
    public double HorizontalGap { get => (double)GetValue(HorizontalGapProperty); set => SetValue(HorizontalGapProperty, value); }
    public double VerticalGap { get => (double)GetValue(VerticalGapProperty); set => SetValue(VerticalGapProperty, value); }
    public string? PreferredSecondRowKey { get => (string?)GetValue(PreferredSecondRowKeyProperty); set => SetValue(PreferredSecondRowKeyProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var grid = CalculateGridForChildren(availableSize.Width);
        if (grid.Rows == 0) return default;
        double cellWidth = CellWidth(availableSize.Width, grid.Columns, HorizontalGap);
        double maxChildHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            maxChildHeight = Math.Max(maxChildHeight, child.DesiredSize.Height);
        }
        double width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : grid.Columns * MinimumCellWidth + (grid.Columns - 1) * HorizontalGap;
        return new Size(width, grid.Rows * maxChildHeight + (grid.Rows - 1) * VerticalGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var grid = CalculateGridForChildren(finalSize.Width);
        if (grid.Rows == 0) return finalSize;
        double cellWidth = CellWidth(finalSize.Width, grid.Columns, HorizontalGap);
        double cellHeight = Math.Max(0, (finalSize.Height - (grid.Rows - 1) * VerticalGap) / grid.Rows);
        var placements = PlanPlacements(InternalChildren.Cast<UIElement>().Select(GetKey).ToArray(), grid.Rows, grid.Columns, PreferredSecondRowKey);
        for (int index = 0; index < InternalChildren.Count; index++)
        {
            var (row, column) = placements[index];
            InternalChildren[index].Arrange(new Rect(
                column * (cellWidth + HorizontalGap), row * (cellHeight + VerticalGap), cellWidth, cellHeight));
        }
        return finalSize;
    }

    private (int Rows, int Columns) CalculateGridForChildren(double availableWidth)
    {
        var grid = CalculateGrid(InternalChildren.Count, availableWidth, MinimumCellWidth, MaxRows);
        if (InternalChildren.Count >= 2 && PreferredSecondRowKey is { Length: > 0 } key
            && InternalChildren.Cast<UIElement>().Any(child => string.Equals(GetKey(child), key, StringComparison.OrdinalIgnoreCase)))
            return (2, (int)Math.Ceiling(InternalChildren.Count / 2d));
        return grid;
    }

    internal static IReadOnlyList<(int Row, int Column)> PlanPlacements(IReadOnlyList<string?> keys, int rows, int columns, string? preferredSecondRowKey)
    {
        var result = new (int Row, int Column)[keys.Count];
        var free = new List<(int Row, int Column)>();
        for (int column = 0; column < columns; column++)
            for (int row = 0; row < rows; row++) free.Add((row, column));

        int preferredIndex = preferredSecondRowKey is null ? -1 : keys.ToList().FindIndex(key => string.Equals(key, preferredSecondRowKey, StringComparison.OrdinalIgnoreCase));
        if (preferredIndex >= 0 && rows > 1)
        {
            var preferredCell = (Row: 1, Column: columns - 1);
            result[preferredIndex] = preferredCell;
            free.Remove(preferredCell);
        }
        int gpuIndex = preferredIndex >= 0 ? keys.ToList().FindIndex(key => string.Equals(key, "gpu", StringComparison.OrdinalIgnoreCase)) : -1;
        if (gpuIndex >= 0 && columns > 1)
        {
            var gpuCell = (Row: 0, Column: Math.Min(1, columns - 1));
            result[gpuIndex] = gpuCell;
            free.Remove(gpuCell);
        }
        for (int index = 0; index < keys.Count; index++)
        {
            if (index == preferredIndex || index == gpuIndex) continue;
            result[index] = free[0];
            free.RemoveAt(0);
        }
        return result;
    }

    private static string? GetKey(UIElement child) => (child as FrameworkElement)?.DataContext is Layout.PodViewModel pod ? pod.Key : null;

    internal static (int Rows, int Columns) CalculateGrid(int itemCount, double availableWidth, double minimumCellWidth, int maxRows)
    {
        if (itemCount <= 0) return (0, 0);
        int safeMaxRows = Math.Max(1, maxRows);
        int columnsThatFit = double.IsFinite(availableWidth) && availableWidth > 0
            ? Math.Max(1, (int)Math.Floor(availableWidth / Math.Max(1, minimumCellWidth)))
            : itemCount;
        int rows = Math.Clamp((int)Math.Ceiling((double)itemCount / columnsThatFit), 1, safeMaxRows);
        return (rows, (int)Math.Ceiling((double)itemCount / rows));
    }

    internal static double CellWidth(double width, int columns, double gap) =>
        double.IsFinite(width) ? Math.Max(0, (width - (columns - 1) * gap) / columns) : 0;
}
