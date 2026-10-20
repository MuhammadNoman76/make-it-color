using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MakeItColor.Controls;

/// <summary>
/// Draws a 20 x 20 icon geometry (Fluent System Icons) scaled to the element's size, in the inherited
/// text colour, so icons follow the theme and their button's hover / disabled states automatically.
/// </summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IconView()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var data = Data;
        if (data == null) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 20.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - 20 * scale) / 2, (ActualHeight - 20 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Foreground, null, data);
        dc.Pop();
        dc.Pop();
    }
}
