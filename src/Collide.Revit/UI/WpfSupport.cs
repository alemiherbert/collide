using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Collide.Findings;
using Collide.Presentation;
using Collide.Presentation.ViewModels;
using Color = System.Windows.Media.Color;
using Visibility = System.Windows.Visibility;

namespace Collide.Revit.UI;

/// <summary>Posts monitor updates (raised on background threads) to the WPF dispatcher of the pane.</summary>
internal sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public sealed class SeverityBrushConverter : IValueConverter
{
    private static readonly Brush Critical = Frozen(Color.FromRgb(0xB0, 0x00, 0x20));
    private static readonly Brush High = Frozen(Color.FromRgb(0xE0, 0x4A, 0x1F));
    private static readonly Brush Medium = Frozen(Color.FromRgb(0xE8, 0x9B, 0x16));
    private static readonly Brush Low = Frozen(Color.FromRgb(0xC9, 0xB6, 0x2A));
    private static readonly Brush Info = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => Critical,
        Severity.High => High,
        Severity.Medium => Medium,
        Severity.Low => Low,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Shows the flat list when grouping is None (ShowWhenNone=true) or the grouped list otherwise.</summary>
public sealed class GroupingVisibilityConverter : IValueConverter
{
    public bool ShowWhenNone { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is IssueGrouping.None) == ShowWhenNone ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
