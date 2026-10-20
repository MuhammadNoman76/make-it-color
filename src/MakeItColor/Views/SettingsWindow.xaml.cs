using System.Diagnostics;
using System.Windows;
using MakeItColor.Controls;
using MakeItColor.Core;
using MakeItColor.Core.Ai;
using MakeItColor.Themes;

namespace MakeItColor.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = Settings.Current;
        ProcessorBox.SelectedIndex = (int)s.Processor;
        SteadyBox.IsChecked = s.SteadyColors;
        FormatBox.SelectedIndex = (int)s.PhotoFormat;
        QualityBox.SelectedIndex = (int)s.VideoQuality;
        ThemeBox.SelectedIndex = (int)s.Theme;
        ToneBox.IsChecked = s.FixTone;
        EnhancePhotosBox.IsChecked = s.EnhancePhotos;
        PhotoSizeBox.SelectedIndex = (int)s.PhotoSize;
        VideoEnhanceBox.SelectedIndex = (int)s.VideoEnhance;
        PhotoSizeBox.IsEnabled = s.EnhancePhotos;
        AutoUpdateBox.IsChecked = s.CheckForUpdates;
        AutoUpdateBox.Checked += (_, _) => s.CheckForUpdates = true;
        AutoUpdateBox.Unchecked += (_, _) => s.CheckForUpdates = false;
        ProcessorNote.Text = Engine.Current is { } d
            ? $"Now: the {d.Kind} ({d.Name}), about {Engine.MillisecondsPerPicture / 1000:0.0} s a picture"
            : "Auto uses the fastest chip that gives correct colours";
        AboutText.Text = $"MakeItColor {AppInfo.VersionText} ({AppInfo.Edition}) · Muhammad Noman";

        ProcessorBox.SelectionChanged += (_, _) => s.Processor = (Processor)Math.Max(0, ProcessorBox.SelectedIndex);
        SteadyBox.Checked += (_, _) => s.SteadyColors = true;
        SteadyBox.Unchecked += (_, _) => s.SteadyColors = false;
        FormatBox.SelectionChanged += (_, _) => s.PhotoFormat = (PhotoFormat)Math.Max(0, FormatBox.SelectedIndex);
        QualityBox.SelectionChanged += (_, _) => s.VideoQuality = (VideoQuality)Math.Max(0, QualityBox.SelectedIndex);
        ToneBox.Checked += (_, _) => s.FixTone = true;
        ToneBox.Unchecked += (_, _) => s.FixTone = false;
        EnhancePhotosBox.Checked += (_, _) => PhotoSizeBox.IsEnabled = s.EnhancePhotos = true;
        EnhancePhotosBox.Unchecked += (_, _) => PhotoSizeBox.IsEnabled = s.EnhancePhotos = false;
        PhotoSizeBox.SelectionChanged += (_, _) => s.PhotoSize = (PhotoSize)Math.Max(0, PhotoSizeBox.SelectedIndex);
        VideoEnhanceBox.SelectionChanged += (_, _) => s.VideoEnhance = (VideoEnhance)Math.Max(0, VideoEnhanceBox.SelectedIndex);
        ThemeBox.SelectionChanged += (_, _) =>
        {
            s.Theme = (AppTheme)Math.Max(0, ThemeBox.SelectedIndex);
            ThemeManager.SetMode(s.Theme);
        };
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateNote.Text = "Checking…";
        UpdateNote.Text = await UpdateService.CheckAsync(manual: true);
        CheckNowButton.IsEnabled = true;
    }

    private void OnWebsite(object sender, RoutedEventArgs e) => Open(AppInfo.Website);

    private void OnSource(object sender, RoutedEventArgs e) => Open(AppInfo.Repository);

    private void OnLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppInfo.RoamingDirectory);
        Open(AppInfo.RoamingDirectory);
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "Open " + target); }
    }
}
