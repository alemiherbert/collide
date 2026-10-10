using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Collide.Presentation.ViewModels;

namespace Collide.Revit.UI;

/// <summary>Dockable pane content. All behaviour lives in <see cref="ClashPaneViewModel"/> (unit-tested without Revit).</summary>
public partial class ClashPaneView
{
    public static readonly IValueConverter NullToCollapsed = new NullVisibilityConverter();

    public ClashPaneView(ClashPaneViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private sealed class NullVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
