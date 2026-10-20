using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MakeItColor.Controls;
using MakeItColor.Core;
using MakeItColor.Themes;

namespace MakeItColor.Views;

/// <summary>
/// Real screenshots of the app for the website and the docs, without showing anything on screen: the window opens far
/// off-screen (not activated, not in the taskbar), is put in a state, rendered to a PNG and closed.
///   MakeItColor.exe --capture home|photo|video|settings out.png [--input file...] [--split 0.5] [--theme light|dark]
///                   [--size 1400x880] [--at 0.3]   (video: capture once this share of the frames is done)
/// </summary>
internal static class Capture
{
    public static bool Handles(string[] args) => args.Contains("--capture");

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var i = Array.IndexOf(args, "--capture");
            var scene = args[i + 1];
            var output = Path.GetFullPath(args[i + 2]);
            if (Value(args, "--theme") is { } theme && Enum.TryParse<AppTheme>(theme, true, out var mode)) ThemeManager.SetMode(mode);
            var size = (Value(args, "--size") ?? "1400x880").Split('x');
            double w = double.Parse(size[0]), h = double.Parse(size[1]);
            var inputs = Values(args, "--input");

            FluentWindow window;
            if (scene == "settings")
            {
                window = new SettingsWindow { Width = w, Height = h };
            }
            else
            {
                var main = new MainWindow { Width = w, Height = h };
                window = main;
            }
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();

            if (window is MainWindow mw && inputs.Count > 0)
            {
                mw.AddFiles(inputs);
                var at = double.TryParse(Value(args, "--at"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0.3;
                var deadline = DateTime.UtcNow.AddMinutes(30);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250);
                    var items = mw.Items;
                    if (scene == "video" && items.OfType<VideoItem>().FirstOrDefault() is { } v && (v.Progress >= at || v.State != ItemState.Working && v.State != ItemState.Waiting))
                        break;
                    if (scene == "photo" && items.Count > 0 && items.All(x => x.State is ItemState.Done or ItemState.Failed)) break;
                }
                if (double.TryParse(Value(args, "--split"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var split))
                    mw.SetSplit(split);
                await Task.Delay(600); // let the last preview arrive
            }
            else
            {
                await Task.Delay(400);
            }

            Render(window, output);
            if (window is MainWindow m) m.CancelAll();
            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture");
            return 1;
        }
    }

    private static void Render(Window window, string output)
    {
        var root = (FrameworkElement)window.Content;
        if (root is System.Windows.Controls.Panel panel) panel.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Window.Solid");
        else if (root is System.Windows.Controls.Control control) control.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Window.Solid");
        root.UpdateLayout();
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var file = File.Create(output);
        encoder.Save(file);
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Values(string[] args, string name)
    {
        var list = new List<string>();
        for (var i = Array.IndexOf(args, name) + 1; i > 0 && i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++) list.Add(args[i]);
        return list;
    }
}
