using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MakeItColor.Interop;
using MakeItColor.Themes;

namespace MakeItColor.Controls;

/// <summary>
/// Base for every Trove window: Windows 11 Mica backdrop where available (solid colour elsewhere),
/// a title bar that follows the light / dark theme, Segoe UI Variable, and live theme switching.
/// </summary>
public class FluentWindow : Window
{
    private IntPtr _hwnd;

    public FluentWindow()
    {
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 14;
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(BackgroundProperty, "Window.Background");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    /// <summary>Dialog frame: no minimize / maximize buttons, not in the taskbar when it has an owner.</summary>
    public bool IsDialog { get; set; }

    public bool UsesMica { get; private set; }

    public IntPtr Handle => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        FitToScreen();
        if (IsDialog)
        {
            Native.MakeDialogFrame(_hwnd);
            if (Owner != null) ShowInTaskbar = false;
        }
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyBackdrop();
    }

    /// <summary>
    /// Makes sure the window fits on its monitor (with display scaling a 1200 x 800 window can be larger than the
    /// screen): too large a window is made smaller and centred again. Windows placed by code (screenshots) are left alone.
    /// </summary>
    private void FitToScreen()
    {
        if (WindowStartupLocation == WindowStartupLocation.Manual) return;
        var owner = Owner != null ? new WindowInteropHelper(Owner).Handle : IntPtr.Zero;
        if (Native.WorkArea(owner) is not { } area) area = SystemParameters.WorkArea;
        const double margin = 16;
        var maxWidth = Math.Max(320, area.Width - 2 * margin);
        var maxHeight = Math.Max(240, area.Height - 2 * margin);
        if (MinWidth > maxWidth) MinWidth = maxWidth;
        if (MinHeight > maxHeight) MinHeight = maxHeight;
        var shrunk = false;
        if (SizeToContent is SizeToContent.Manual or SizeToContent.Height && !double.IsNaN(Width) && Width > maxWidth)
        {
            Width = maxWidth;
            shrunk = true;
        }
        if (SizeToContent is SizeToContent.Manual or SizeToContent.Width && !double.IsNaN(Height) && Height > maxHeight)
        {
            Height = maxHeight;
            shrunk = true;
        }
        if (SizeToContent != SizeToContent.Manual) MaxHeight = maxHeight;
        if (shrunk || Owner == null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var w = double.IsNaN(Width) ? ActualWidth : Width;
            var h = double.IsNaN(Height) ? ActualHeight : Height;
            Left = area.Left + Math.Max(0, (area.Width - w) / 2);
            Top = area.Top + Math.Max(0, (area.Height - h) / 2);
        }
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnThemeChanged));
            return;
        }
        if (_hwnd != IntPtr.Zero) ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        Native.SetDarkTitleBar(_hwnd, ThemeManager.IsDark);
        var source = HwndSource.FromHwnd(_hwnd);
        if (Native.TrySetMica(_hwnd) && source?.CompositionTarget != null)
        {
            UsesMica = true;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
        }
        else
        {
            UsesMica = false;
            SetResourceReference(BackgroundProperty, "Window.Background");
        }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            // "ImmersiveColorSet" (light / dark switch) arrives as WM_SETTINGCHANGE; let it settle first.
            Dispatcher.BeginInvoke(new Action(ThemeManager.OnSystemSettingsChanged), System.Windows.Threading.DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }
}
