using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Automation;

namespace MakeItColor.Controls;

/// <summary>
/// Before / after: the original on the left of a divider, the colour version on the right. Drag the divider (or click,
/// or use the arrow keys) to compare. With only a "before" picture it shows that alone.
/// </summary>
public sealed class CompareView : Grid
{
    private readonly Viewbox _box = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    private readonly Grid _canvas = new();
    private readonly Image _before = new() { Stretch = Stretch.Fill };
    private readonly Image _after = new() { Stretch = Stretch.Fill };
    private readonly RectangleGeometry _clip = new();
    private readonly Rectangle _line = new() { Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly Border _handle = new()
    {
        Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.35 },
    };
    private readonly Border _beforeLabel = Label("Before");
    private readonly Border _afterLabel = Label("After");
    private double _split = 0.5;
    private bool _dragging;

    public CompareView()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeWE;
        _after.Clip = _clip;
        _handle.Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 6,0 L 0,6 L 6,12 M 16,0 L 22,6 L 16,12"), Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _canvas.Children.Add(_before);
        _canvas.Children.Add(_after);
        _canvas.Children.Add(_line);
        _canvas.Children.Add(_handle);
        _box.Child = _canvas;
        Children.Add(_box);
        _beforeLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _afterLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(_beforeLabel);
        Children.Add(_afterLabel);
        SizeChanged += (_, _) => Layout();
        AutomationProperties.SetName(this, "Before and after comparison");
    }

    private static Border Label(string text) => new()
    {
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12), Padding = new Thickness(10, 4, 10, 5), CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), IsHitTestVisible = false,
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold },
    };

    public BitmapSource? Before
    {
        get => _before.Source as BitmapSource;
        set
        {
            _before.Source = value;
            Layout();
        }
    }

    public BitmapSource? After
    {
        get => _after.Source as BitmapSource;
        set
        {
            _after.Source = value;
            Layout();
        }
    }

    /// <summary>Where the divider is, 0 (all colour) to 1 (all original).</summary>
    public double Split
    {
        get => _split;
        set
        {
            _split = Math.Clamp(value, 0, 1);
            Layout();
        }
    }

    private void Layout()
    {
        var source = _before.Source as BitmapSource ?? _after.Source as BitmapSource;
        if (source == null)
        {
            _canvas.Visibility = Visibility.Collapsed;
            _beforeLabel.Visibility = _afterLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _canvas.Visibility = Visibility.Visible;
        double w = source.PixelWidth, h = source.PixelHeight;
        _canvas.Width = w;
        _canvas.Height = h;
        var compare = _after.Source != null && _before.Source != null;
        _after.Visibility = _after.Source != null ? Visibility.Visible : Visibility.Collapsed;
        _line.Visibility = _handle.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        _beforeLabel.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        _afterLabel.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        Cursor = compare ? Cursors.SizeWE : Cursors.Arrow;
        var x = compare ? w * _split : 0;
        _clip.Rect = new Rect(x, 0, Math.Max(0, w - x), h);

        // Keep the line and handle the same size on screen whatever the picture's size.
        var scale = ActualWidth > 0 && ActualHeight > 0 ? Math.Min(ActualWidth / w, ActualHeight / h) : 1;
        if (scale <= 0) scale = 1;
        var lineWidth = 2 / scale;
        _line.Width = lineWidth;
        _line.Margin = new Thickness(x - lineWidth / 2, 0, 0, 0);
        _handle.LayoutTransform = new ScaleTransform(1 / scale, 1 / scale);
        _handle.Margin = new Thickness(x - 17 / scale, h / 2 - 17 / scale, 0, 0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_after.Source == null || _before.Source == null) return;
        _dragging = true;
        CaptureMouse();
        Move(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Move(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void Move(MouseEventArgs e)
    {
        if (_canvas.Width is not > 0) return;
        Split = e.GetPosition(_canvas).X / _canvas.Width;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Left) { Split -= 0.05; e.Handled = true; }
        else if (e.Key == Key.Right) { Split += 0.05; e.Handled = true; }
        else if (e.Key == Key.Home) { Split = 0; e.Handled = true; }
        else if (e.Key == Key.End) { Split = 1; e.Handled = true; }
    }
}
