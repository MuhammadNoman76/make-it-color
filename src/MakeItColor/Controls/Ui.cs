using System.Windows;
using System.Windows.Media;

namespace MakeItColor.Controls;

/// <summary>Attached properties the control templates in Themes\Theme.xaml read.</summary>
public static class Ui
{
    /// <summary>Grey hint text shown in an empty text box.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? value) => o.SetValue(PlaceholderProperty, value);

    /// <summary>Icon (20 x 20 geometry) for buttons and menu items.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject o) => (Geometry?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, Geometry? value) => o.SetValue(IconProperty, value);

    /// <summary>Corner radius for templates that support it.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(4)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius value) => o.SetValue(CornerRadiusProperty, value);

    /// <summary>Secondary line under a setting's title (settings cards).</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached(
        "Description", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetDescription(DependencyObject o) => (string?)o.GetValue(DescriptionProperty);
    public static void SetDescription(DependencyObject o, string? value) => o.SetValue(DescriptionProperty, value);
}
