using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MakeItColor.Interop;
using MakeItColor.Core;

namespace MakeItColor.Themes;

/// <summary>
/// Light / dark colours and the system accent colour, following Windows (or the user's choice in Settings).
/// The colours are the Windows 11 (WinUI) design tokens; every brush is a DynamicResource, so switching
/// the theme updates open windows at once.
/// </summary>
internal static class ThemeManager
{
    private static ResourceDictionary? s_dictionary;
    private static AppTheme s_mode = AppTheme.System;

    public static bool IsDark { get; private set; }

    /// <summary>The accent colour and its lighter / darker shades (Windows' AccentPalette).</summary>
    public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);
    public static Color AccentLight1 { get; private set; }
    public static Color AccentLight2 { get; private set; }
    public static Color AccentLight3 { get; private set; }
    public static Color AccentDark1 { get; private set; }
    public static Color AccentDark2 { get; private set; }
    public static Color AccentDark3 { get; private set; }

    /// <summary>Raised after the theme or accent colour changed (windows update their title bars).</summary>
    public static event Action? Changed;

    public static void Initialize(AppTheme mode)
    {
        s_mode = mode;
        Apply();
    }

    public static void SetMode(AppTheme mode)
    {
        if (mode == s_mode) return;
        s_mode = mode;
        Apply();
    }

    /// <summary>Windows told us the theme, accent colour or colorization changed.</summary>
    public static void OnSystemSettingsChanged()
    {
        var dark = s_mode == AppTheme.Dark || (s_mode == AppTheme.System && SystemPrefersDark());
        var accent = ReadAccent();
        if (dark == IsDark && accent == Accent && s_dictionary != null) return;
        Apply();
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color ReadAccent()
    {
        var palette = ReadPalette();
        return palette?[3] ?? Accent;
    }

    /// <summary>The 8 colours of HKCU\...\Explorer\Accent\AccentPalette (Windows 8+): light 3..1, accent, dark 1..3.</summary>
    private static Color[]? ReadPalette()
    {
        try
        {
            if (Native.OsVersion.Major < 10) return null; // Windows 7/8.1: their colorization isn't an accent colour
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] bytes && bytes.Length >= 28)
            {
                var colors = new Color[7];
                for (var i = 0; i < 7; i++) colors[i] = Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
                return colors;
            }
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
            {
                var c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                return Derive(c);
            }
        }
        catch
        {
        }
        return null;
    }

    private static Color[] Derive(Color c) => new[]
    {
        Mix(c, Colors.White, 0.75), Mix(c, Colors.White, 0.5), Mix(c, Colors.White, 0.25), c,
        Mix(c, Colors.Black, 0.2), Mix(c, Colors.Black, 0.4), Mix(c, Colors.Black, 0.6),
    };

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static void Apply()
    {
        IsDark = s_mode == AppTheme.Dark || (s_mode == AppTheme.System && SystemPrefersDark());
        var palette = ReadPalette() ?? Derive(Color.FromRgb(0x00, 0x78, 0xD4));
        AccentLight3 = palette[0];
        AccentLight2 = palette[1];
        AccentLight1 = palette[2];
        Accent = palette[3];
        AccentDark1 = palette[4];
        AccentDark2 = palette[5];
        AccentDark3 = palette[6];

        var d = new ResourceDictionary();
        void Brush(string key, uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            d[key] = b;
        }

        void BrushOf(string key, Color color, byte alpha = 255)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            b.Freeze();
            d[key] = b;
        }

        var mica = Native.SupportsMica;
        if (IsDark)
        {
            Brush("Window.Background", 0xFF202020);
            Brush("Window.Solid", 0xFF202020);
            Brush("Layer.Background", mica ? 0x4C3A3A3Au : 0xFF272727);
            Brush("Layer.Stroke", 0x19000000);
            Brush("Card.Background", 0x0DFFFFFF);
            Brush("Card.BackgroundSecondary", 0x08FFFFFF);
            Brush("Card.Stroke", 0x19000000);
            Brush("Text.Primary", 0xFFFFFFFF);
            Brush("Text.Secondary", 0xC5FFFFFF);
            Brush("Text.Tertiary", 0x87FFFFFF);
            Brush("Text.Disabled", 0x5DFFFFFF);
            Brush("Text.OnAccent", 0xFF000000);
            Brush("Text.OnAccentDisabled", 0x87FFFFFF);
            Brush("Text.OnAccentSecondary", 0x80000000);
            Brush("Control.StrokeOnAccent", 0x14FFFFFF);
            Brush("Control.StrokeOnAccentSecondary", 0x23000000);
            BrushOf("Text.Accent", AccentLight3);
            Brush("Control.Fill", 0x0FFFFFFF);
            Brush("Control.FillSecondary", 0x15FFFFFF);
            Brush("Control.FillTertiary", 0x08FFFFFF);
            Brush("Control.FillDisabled", 0x0BFFFFFF);
            Brush("Control.FillInputActive", 0xB31E1E1E);
            Brush("Control.Stroke", 0x12FFFFFF);
            Brush("Control.StrokeSecondary", 0x18FFFFFF);
            Brush("Control.StrongStroke", 0x8BFFFFFF);
            Brush("Control.StrongFill", 0x8BFFFFFF);
            Brush("Control.AltFill", 0x19000000);
            Brush("Subtle.Fill", 0x00FFFFFF);
            Brush("Subtle.FillSecondary", 0x0FFFFFFF);
            Brush("Subtle.FillTertiary", 0x0AFFFFFF);
            BrushOf("Accent.Fill", AccentLight2);
            BrushOf("Accent.FillSecondary", AccentLight2, 0xE6);
            BrushOf("Accent.FillTertiary", AccentLight2, 0xCC);
            Brush("Accent.FillDisabled", 0x28FFFFFF);
            BrushOf("Accent.Selection", AccentLight2, 0x26);
            BrushOf("Accent.SelectionHover", AccentLight2, 0x33);
            Brush("Divider", 0x15FFFFFF);
            Brush("Flyout.Background", 0xFF2C2C2C);
            Brush("Flyout.Stroke", 0x33000000);
            Brush("Focus.Outer", 0xFFFFFFFF);
            Brush("Focus.Inner", 0xB3000000);
            Brush("Status.Success", 0xFF6CCB5F);
            Brush("Status.Caution", 0xFFFCE100);
            Brush("Status.Critical", 0xFFFF99A4);
            Brush("Status.SuccessBackground", 0xFF393D1B);
            Brush("Status.CautionBackground", 0xFF433519);
            Brush("Status.CriticalBackground", 0xFF442726);
            Brush("Status.InfoBackground", 0x08FFFFFF);
            Brush("Smoke", 0x4D000000);
            Brush("ScrollBar.Thumb", 0x8BFFFFFF);
            Brush("Shadow", 0x66000000);
        }
        else
        {
            Brush("Window.Background", 0xFFF3F3F3);
            Brush("Window.Solid", 0xFFF3F3F3);
            Brush("Layer.Background", mica ? 0x80FFFFFFu : 0xFFF9F9F9);
            Brush("Layer.Stroke", 0x0F000000);
            Brush("Card.Background", 0xB3FFFFFF);
            Brush("Card.BackgroundSecondary", 0x80F6F6F6);
            Brush("Card.Stroke", 0x0F000000);
            Brush("Text.Primary", 0xE4000000);
            Brush("Text.Secondary", 0x9E000000);
            Brush("Text.Tertiary", 0x72000000);
            Brush("Text.Disabled", 0x5C000000);
            Brush("Text.OnAccent", 0xFFFFFFFF);
            Brush("Text.OnAccentDisabled", 0xFFFFFFFF);
            Brush("Text.OnAccentSecondary", 0xB3FFFFFF);
            Brush("Control.StrokeOnAccent", 0x14FFFFFF);
            Brush("Control.StrokeOnAccentSecondary", 0x66000000);
            BrushOf("Text.Accent", AccentDark2);
            Brush("Control.Fill", 0xB3FFFFFF);
            Brush("Control.FillSecondary", 0x80F9F9F9);
            Brush("Control.FillTertiary", 0x4DF9F9F9);
            Brush("Control.FillDisabled", 0x4DF9F9F9);
            Brush("Control.FillInputActive", 0xFFFFFFFF);
            Brush("Control.Stroke", 0x0F000000);
            Brush("Control.StrokeSecondary", 0x29000000);
            Brush("Control.StrongStroke", 0x9C000000);
            Brush("Control.StrongFill", 0x72000000);
            Brush("Control.AltFill", 0x06000000);
            Brush("Subtle.Fill", 0x00FFFFFF);
            Brush("Subtle.FillSecondary", 0x09000000);
            Brush("Subtle.FillTertiary", 0x06000000);
            BrushOf("Accent.Fill", AccentDark1);
            BrushOf("Accent.FillSecondary", AccentDark1, 0xE6);
            BrushOf("Accent.FillTertiary", AccentDark1, 0xCC);
            Brush("Accent.FillDisabled", 0x37000000);
            BrushOf("Accent.Selection", AccentLight2, 0x40);
            BrushOf("Accent.SelectionHover", AccentLight2, 0x55);
            Brush("Divider", 0x0F000000);
            Brush("Flyout.Background", 0xFFF9F9F9);
            Brush("Flyout.Stroke", 0x0F000000);
            Brush("Focus.Outer", 0xE4000000);
            Brush("Focus.Inner", 0xB3FFFFFF);
            Brush("Status.Success", 0xFF0F7B0F);
            Brush("Status.Caution", 0xFF9D5D00);
            Brush("Status.Critical", 0xFFC42B1C);
            Brush("Status.SuccessBackground", 0xFFDFF6DD);
            Brush("Status.CautionBackground", 0xFFFFF4CE);
            Brush("Status.CriticalBackground", 0xFFFDE7E9);
            Brush("Status.InfoBackground", 0x80F6F6F6);
            Brush("Smoke", 0x4D000000);
            Brush("ScrollBar.Thumb", 0x72000000);
            Brush("Shadow", 0x33000000);
        }
        BrushOf("Accent.Base", Accent);

        var app = Application.Current;
        if (app != null)
        {
            var merged = app.Resources.MergedDictionaries;
            if (s_dictionary != null) merged.Remove(s_dictionary);
            merged.Insert(0, d);
        }
        s_dictionary = d;
        Changed?.Invoke();
    }
}
