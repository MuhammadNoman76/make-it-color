using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MakeItColor.Controls;
using MakeItColor.Core;
using MakeItColor.Core.Ai;
using MakeItColor.Core.Video;
using Microsoft.Win32;

namespace MakeItColor.Views;

public partial class MainWindow : FluentWindow
{
    private const int PreviewSize = 1600;

    private readonly ObservableCollection<WorkItem> _items = new();
    private readonly DispatcherTimer _vividTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _processing;
    private bool _updatingSlider;

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        Engine.StatusChanged += s => Dispatcher.BeginInvoke(() => ShowEngineStatus(s));
        _vividTimer.Tick += (_, _) =>
        {
            _vividTimer.Stop();
            RefreshPhotoPreview();
        };
        Vividness.Value = Settings.Current.Vividness;
        UpdateVividText();

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => HighlightDrop(false);
        Drop += OnDrop;
        InputBindings.Add(new KeyBinding(new Command(() => OnOpen(this, new RoutedEventArgs())), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => OnSave(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => OnSettings(this, new RoutedEventArgs())), Key.OemComma, ModifierKeys.Control));
        Closing += OnClosing;
        UpdateService.Changed += () => Dispatcher.BeginInvoke(ShowUpdate);
        SizeChanged += (_, _) => Relayout();
        Toolbar.SizeChanged += (_, _) => LayoutToolbar();

        // Get the model ready early (download and load in the background), so the first picture is quick.
        Loaded += async (_, _) =>
        {
            UpdateService.Start();
            if (ModelStore.IsAvailable(ModelStore.Half) || ModelStore.IsAvailable(ModelStore.Full))
            {
                try { await Engine.PrepareAsync(null, CancellationToken.None); }
                catch (Exception ex) { ShowEngineStatus(ex.Message); }
            }
        };
    }

    // ---------------------------------------------------------------------------- fitting the window's size

    /// <summary>Adapts the layout to the window's size, down to the smallest window (560 x 460).</summary>
    private void Relayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        FilesColumn.Width = new GridLength(Math.Clamp(w * 0.24, 200, 300));
        Tagline.Visibility = w < 700 ? Visibility.Collapsed : Visibility.Visible;
        InfoCards.Columns = w >= 980 ? 3 : 1;
        InfoCards.Visibility = w < 980 && h < 900 || h < 620 ? Visibility.Collapsed : Visibility.Visible;
        HomeLogo.Visibility = h < 560 ? Visibility.Collapsed : Visibility.Visible;
        LayoutToolbar();
    }

    /// <summary>The photo tools and the buttons share one row when there is room, otherwise the buttons go below.</summary>
    private void LayoutToolbar()
    {
        var width = Toolbar.ActualWidth;
        if (width <= 0) return;
        var compact = width < 560;
        VividnessLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Vividness.Width = compact ? 120 : 180;
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        PhotoTools.Measure(infinite);
        ActionButtons.Measure(infinite);
        var stacked = PhotoTools.DesiredSize.Width + ActionButtons.DesiredSize.Width + 16 > width;
        System.Windows.Controls.Grid.SetRow(ActionButtons, stacked ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(ActionButtons, stacked ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(ActionButtons, stacked ? 2 : 1);
        ActionButtons.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(0);
    }

    /// <summary>For screenshots (Capture).</summary>
    internal IReadOnlyList<WorkItem> Items => _items;

    internal void SetSplit(double split) => Compare.Split = split;

    internal void CancelAll()
    {
        foreach (var video in _items.OfType<VideoItem>()) video.Cancel.Cancel();
    }

    // ---------------------------------------------------------------------------- adding files

    public void AddFiles(IEnumerable<string> paths)
    {
        var added = new List<WorkItem>();
        foreach (var path in Expand(paths))
        {
            if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase) && i.State != ItemState.Failed)) continue;
            WorkItem item = PictureIO.IsVideo(path) ? new VideoItem(path) : new PhotoItem(path);
            _items.Add(item);
            added.Add(item);
        }
        if (added.Count == 0)
        {
            StatusText.Text = "Nothing to add: choose photos (JPEG, PNG, TIFF…) or videos (MP4, MOV, MKV…).";
            return;
        }
        HomePanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Visible;
        if (List.SelectedItem == null) List.SelectedItem = added[0];
        _ = ProcessQueueAsync();
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { continue; }
                foreach (var f in files)
                    if (PictureIO.IsPhoto(f) || PictureIO.IsVideo(f)) yield return f;
            }
            else if (File.Exists(path) && (PictureIO.IsPhoto(path) || PictureIO.IsVideo(path)))
            {
                yield return path;
            }
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var photos = string.Join(";", PictureIO.PhotoExtensions.Select(x => "*" + x));
        var videos = string.Join(";", PictureIO.VideoExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "Choose photos or videos to colour",
            Multiselect = true,
            Filter = $"Photos and videos|{photos};{videos}|Photos|{photos}|Videos|{videos}|All files|*.*",
            InitialDirectory = Settings.Current.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        Settings.Current.LastFolder = Path.GetDirectoryName(dialog.FileNames[0]);
        AddFiles(dialog.FileNames);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of photos or videos" };
        if (dialog.ShowDialog(this) != true) return;
        AddFiles(new[] { dialog.FolderName });
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        HighlightDrop(ok);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        HighlightDrop(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
    }

    private void HighlightDrop(bool on)
    {
        DropBorder.Opacity = on ? 1 : 0.6;
        DropBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Accent.Fill" : "Control.StrongStroke");
    }

    // ---------------------------------------------------------------------------- the queue

    private async Task ProcessQueueAsync()
    {
        if (_processing) return;
        _processing = true;
        try
        {
            while (_items.FirstOrDefault(i => i.State == ItemState.Waiting) is { } item)
            {
                if (item is PhotoItem photo) await ColorPhotoAsync(photo);
                else if (item is VideoItem video) await ColorVideoAsync(video);
                UpdateButtons();
            }
        }
        finally
        {
            _processing = false;
            UpdateButtons();
        }
    }

    private async Task ColorPhotoAsync(PhotoItem item)
    {
        item.State = ItemState.Working;
        item.Status = "Opening…";
        Show(item);
        try
        {
            var clock = Stopwatch.StartNew();
            var source = await Task.Run(() => PictureIO.Load(item.Path));
            item.Width = source.Width;
            item.Height = source.Height;
            var beforePreview = await Task.Run(() => Downscale(source, PreviewSize));
            item.Before = PictureIO.ToBitmap(beforePreview);
            item.Thumbnail = PictureIO.Preview(beforePreview, 160);
            Show(item);

            var status = new Progress<string>(s => SetStatus(item, s));
            clock.Restart();
            await Engine.PrepareAsync(status, CancellationToken.None);

            // Brightness and contrast, then the enhancer (kept on disk for saving at full size).
            var prepared = new Prepared(source, source);
            if (PhotoPrep.Wanted(item.Enhance))
            {
                var percent = new Progress<double>(p => SetStatus(item, $"Enhancing… {p:0%}"));
                prepared = await PhotoPrep.PrepareAsync(source, item.Enhance, status, percent, CancellationToken.None);
                if (!ReferenceEquals(prepared.Detail, source))
                {
                    var keep = prepared.Detail;
                    item.PreparedFile = await Task.Run(() => PhotoPrep.Keep(keep));
                }
            }
            var work = prepared.Detail;
            var preview = ReferenceEquals(work, source) ? beforePreview : await Task.Run(() => Downscale(work, PreviewSize));
            item.PreviewSource = preview;

            SetStatus(item, "Colouring…");
            var (ab, device) = await Task.Run(() =>
            {
                var input = new float[3 * ColorModel.Plane];
                var smallL = new float[ColorModel.Plane];
                ColorMath.ModelInput(prepared.ForModel, input, smallL);
                return Engine.Run(input);
            });
            item.Ab = ab;
            var vivid = item.Vividness;
            var after = await Task.Run(() => new Colorizer().Finish(preview, ab, vivid));
            item.After = PictureIO.ToBitmap(after);
            item.Thumbnail = PictureIO.Preview(after, 160);
            item.State = ItemState.Done;
            var size = work.Width != item.Width ? $"{item.Width} × {item.Height} → {work.Width} × {work.Height}" : $"{item.Width} × {item.Height}";
            var enhanced = item.Enhance && Settings.Current.EnhancePhotos ? " · enhanced" : "";
            item.Status = $"Done in {clock.Elapsed.TotalSeconds:0.0} s on the {device.Kind}{enhanced} · {size}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Colour photo " + item.Path);
            item.State = ItemState.Failed;
            item.Status = "Couldn't colour this photo: " + ex.Message;
            StatusText.Text = item.Status;
        }
        Show(item);
    }

    private void SetStatus(WorkItem item, string status)
    {
        item.Status = status;
        if (ReferenceEquals(List.SelectedItem, item)) WorkingText.Text = status;
    }

    private async Task ColorVideoAsync(VideoItem item)
    {
        item.State = ItemState.Working;
        item.Status = "Opening…";
        if (item.Cancel.IsCancellationRequested) item.Cancel = new CancellationTokenSource();
        item.OutputPath = OutputFor(item.Path);
        Show(item);
        var job = new VideoJob(item.Path, item.OutputPath);
        var progress = new Progress<VideoProgress>(p =>
        {
            item.Progress = p.Total > 0 ? Math.Min(1, (double)p.Done / p.Total) : 0;
            var left = p.Remaining is { } r ? $" · about {Duration(r)} left" : "";
            item.Status = $"Frame {p.Done:N0} of {p.Total:N0} · {p.FramesPerSecond:0.0} frames a second{left} · {p.Device}";
            if (p.Before != null && p.After != null) _ = UpdateVideoPreviewAsync(item, p.Before, p.After);
            if (ReferenceEquals(List.SelectedItem, item)) WorkingText.Text = item.Status;
        });
        var status = new Progress<string>(s => { item.Status = s; if (ReferenceEquals(List.SelectedItem, item)) WorkingText.Text = s; });
        try
        {
            await job.RunAsync(progress, status, item.Cancel.Token);
            item.State = ItemState.Done;
            item.Progress = 1;
            item.Status = $"Saved as {Path.GetFileName(item.OutputPath)}";
        }
        catch (OperationCanceledException)
        {
            item.State = ItemState.Cancelled;
            item.Status = "Stopped. Nothing was saved.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Colour video " + item.Path);
            item.State = ItemState.Failed;
            item.Status = "Couldn't colour this video: " + ex.Message;
            StatusText.Text = item.Status;
        }
        Show(item);
    }

    private async Task UpdateVideoPreviewAsync(VideoItem item, Picture before, Picture after)
    {
        var (b, a) = await Task.Run(() => (PictureIO.Preview(before, 1280), PictureIO.Preview(after, 1280)));
        item.Before = b;
        item.After = a;
        item.Thumbnail ??= PictureIO.Preview(before, 160);
        if (ReferenceEquals(List.SelectedItem, item))
        {
            Compare.Before = b;
            Compare.After = a;
        }
    }

    /// <summary>"name (color).mp4" next to the original; the Videos folder if that folder can't be written to.</summary>
    private static string OutputFor(string input)
    {
        var path = PictureIO.SuggestOutput(input, ".mp4");
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var probe = Path.Combine(dir, $".makeitcolor-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return path;
        }
        catch
        {
            return PictureIO.SuggestOutput(input, ".mp4", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        }
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(t.TotalMinutes)} min" : $"{Math.Max(1, (int)t.TotalSeconds)} s";

    private static Picture Downscale(Picture source, int max)
    {
        var scale = Math.Min(1.0, (double)max / Math.Max(source.Width, source.Height));
        if (scale >= 1) return source;
        BitmapSource bitmap = new TransformedBitmap(PictureIO.ToBitmap(source), new ScaleTransform(scale, scale));
        var small = new Picture(bitmap.PixelWidth, bitmap.PixelHeight) { HasAlpha = source.HasAlpha };
        bitmap.CopyPixels(small.Pixels, small.Stride, 0);
        return small;
    }

    // ---------------------------------------------------------------------------- showing the selected item

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is WorkItem item) Show(item);
    }

    private void Show(WorkItem item)
    {
        if (!ReferenceEquals(List.SelectedItem, item)) return;
        Compare.Before = item.Before;
        Compare.After = item.After;
        WorkingOverlay.Visibility = item.IsWorking ? Visibility.Visible : Visibility.Collapsed;
        WorkingText.Text = item.Status;
        if (item is PhotoItem photo)
        {
            EnhanceBox.IsChecked = photo.Enhance;
            EnhanceBox.IsEnabled = !photo.IsWorking && Settings.Current.EnhancePhotos;
            EnhanceBox.ToolTip = Settings.Current.EnhancePhotos
                ? "Sharper and cleaner: takes out blur, noise and JPEG damage (and makes small photos larger, see Settings)"
                : "Photo enhancing is off in Settings";
            _updatingSlider = true;
            Vividness.Value = photo.Vividness;
            _updatingSlider = false;
            UpdateVividText();
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var item = List.SelectedItem as WorkItem;
        var photo = item as PhotoItem;
        var video = item as VideoItem;
        PhotoTools.Visibility = photo is { IsDone: true } ? Visibility.Visible : Visibility.Hidden;
        SaveButton.Visibility = photo is { IsDone: true } ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = video is { IsWorking: true } ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = item is { State: ItemState.Failed or ItemState.Cancelled } ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = ShowButton.Visibility = video is { IsDone: true } ? Visibility.Visible : Visibility.Collapsed;
        SaveAllButton.IsEnabled = _items.OfType<PhotoItem>().Count(p => p.IsDone) > 0;
        WorkingOverlay.Visibility = item is { IsWorking: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEngineStatus(string status)
    {
        if (!string.IsNullOrEmpty(status)) StatusText.Text = Engine.BackgroundNote is { } note ? $"{status}. {note}" : status;
        if (Engine.Current is { } device) DeviceText.Text = $"{device.Kind} · {device.Name}";
    }

    // ---------------------------------------------------------------------------- colour strength

    private void OnVividnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateVividText();
        if (_updatingSlider) return;
        Settings.Current.Vividness = Math.Round(Vividness.Value, 2);
        if (List.SelectedItem is PhotoItem photo) photo.Vividness = Settings.Current.Vividness;
        _vividTimer.Stop();
        _vividTimer.Start();
    }

    private void OnVividnessReset(object sender, RoutedEventArgs e) => Vividness.Value = 1.0;

    /// <summary>The Enhance switch: colours the selected photo again with or without the enhancer.</summary>
    private void OnEnhanceClick(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not PhotoItem photo || photo.IsWorking) return;
        photo.Enhance = EnhanceBox.IsChecked == true;
        Redo(photo);
    }

    private void Redo(PhotoItem photo)
    {
        DeletePrepared(photo);
        photo.Ab = null;
        photo.After = null;
        photo.State = ItemState.Waiting;
        photo.Status = "Waiting";
        Show(photo);
        _ = ProcessQueueAsync();
    }

    private static void DeletePrepared(PhotoItem photo)
    {
        if (photo.PreparedFile is not { } file) return;
        photo.PreparedFile = null;
        try { File.Delete(file); } catch { /* temp */ }
    }

    private void UpdateVividText() => VividnessText.Text = $"{Math.Round(Vividness.Value * 100):0}%";

    private async void RefreshPhotoPreview()
    {
        if (List.SelectedItem is not PhotoItem { IsDone: true, Ab: { } ab, PreviewSource: { } preview } photo) return;
        var vivid = photo.Vividness;
        var after = await Task.Run(() => new Colorizer().Finish(preview, ab, vivid));
        photo.After = PictureIO.ToBitmap(after);
        if (ReferenceEquals(List.SelectedItem, photo)) Compare.After = photo.After;
    }

    // ---------------------------------------------------------------------------- saving and opening

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not PhotoItem { IsDone: true } photo) return;
        var format = Settings.Current.PhotoFormat;
        var dialog = new SaveFileDialog
        {
            Title = "Save the colour photo",
            FileName = Path.GetFileName(PictureIO.SuggestOutput(photo.Path, format == PhotoFormat.Jpeg ? ".jpg" : ".png")),
            InitialDirectory = Path.GetDirectoryName(photo.Path),
            Filter = "PNG (lossless)|*.png|JPEG|*.jpg",
            FilterIndex = format == PhotoFormat.Jpeg ? 2 : 1,
        };
        if (dialog.ShowDialog(this) != true) return;
        var chosen = dialog.FilterIndex == 2 ? PhotoFormat.Jpeg : PhotoFormat.Png;
        await SavePhotoAsync(photo, dialog.FileName, chosen);
    }

    private async void OnSaveAll(object sender, RoutedEventArgs e)
    {
        var done = _items.OfType<PhotoItem>().Where(p => p.IsDone).ToList();
        if (done.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = "Save the colour photos to…", InitialDirectory = Path.GetDirectoryName(done[0].Path) };
        if (dialog.ShowDialog(this) != true) return;
        var format = Settings.Current.PhotoFormat;
        var saved = 0;
        foreach (var photo in done)
        {
            var path = PictureIO.SuggestOutput(photo.Path, format == PhotoFormat.Jpeg ? ".jpg" : ".png", dialog.FolderName);
            if (await SavePhotoAsync(photo, path, format)) saved++;
        }
        StatusText.Text = $"Saved {saved} colour photo{(saved == 1 ? "" : "s")} to {dialog.FolderName}";
    }

    /// <summary>Colours the full-size photo (the model's output is reused) and saves it.</summary>
    private async Task<bool> SavePhotoAsync(PhotoItem photo, string path, PhotoFormat format)
    {
        if (photo.Ab is not { } ab) return false;
        var previous = photo.Status;
        photo.Status = "Saving…";
        try
        {
            var vivid = photo.Vividness;
            await Task.Run(() =>
            {
                var full = photo.PreparedFile is { } prepared && File.Exists(prepared) ? PhotoPrep.Read(prepared) : PictureIO.Load(photo.Path);
                var color = new Colorizer().Finish(full, ab, vivid);
                PictureIO.Save(color, path, format);
            });
            photo.SavedAs = path;
            photo.Status = $"Saved as {Path.GetFileName(path)}";
            StatusText.Text = $"Saved {path}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save photo " + path);
            photo.Status = previous;
            MessageDialog.Show(this, "Couldn't save the photo", ex.Message, "OK", icon: DialogIcon.Error);
            return false;
        }
    }

    /// <summary>Try again: a failed (or stopped) photo or video goes back in the queue.</summary>
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not WorkItem { State: ItemState.Failed or ItemState.Cancelled } item) return;
        if (item is PhotoItem photo)
        {
            Redo(photo);
            return;
        }
        item.State = ItemState.Waiting;
        item.Status = "Waiting";
        item.Progress = 0;
        Show(item);
        _ = ProcessQueueAsync();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is VideoItem video) video.Cancel.Cancel();
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is VideoItem { OutputPath: { } path } && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is VideoItem { OutputPath: { } path } && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        var before = (s.Processor, s.FixTone, s.EnhancePhotos, s.PhotoSize);
        new SettingsWindow { Owner = this }.ShowDialog();
        Settings.Save();
        if (s.Processor != before.Processor && !_processing)
        {
            Engine.Reset();
            EnhanceEngine.Reset();
            DeviceText.Text = "NPU › GPU › CPU";
            _ = PrepareQuietlyAsync();
        }
        // Photo settings changed: colour the finished photos again with them.
        if ((s.FixTone, s.EnhancePhotos, s.PhotoSize) != (before.FixTone, before.EnhancePhotos, before.PhotoSize))
        {
            foreach (var photo in _items.OfType<PhotoItem>().Where(p => p.State is ItemState.Done or ItemState.Failed).ToList())
            {
                if (s.EnhancePhotos != before.EnhancePhotos) photo.Enhance = s.EnhancePhotos;
                Redo(photo);
            }
        }
        if (List.SelectedItem is WorkItem selected) Show(selected);
    }

    private static async Task PrepareQuietlyAsync()
    {
        try { await Engine.PrepareAsync(null, CancellationToken.None); }
        catch (Exception ex) { Log.Error(ex, "Prepare after settings change"); }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var working = _items.OfType<VideoItem>().Where(v => v.IsWorking).ToList();
        if (working.Count > 0)
        {
            var choice = MessageDialog.Show(this, "Stop colouring the video?",
                "A video is still being coloured. If you close MakeItColor now, it stops and that video isn't saved.",
                "Stop and close", close: "Keep going", icon: DialogIcon.Warning);
            if (choice != DialogChoice.Primary)
            {
                e.Cancel = true;
                return;
            }
            foreach (var v in working) v.Cancel.Cancel();
        }
        Settings.Save();
        foreach (var photo in _items.OfType<PhotoItem>()) DeletePrepared(photo);
    }

    // ---------------------------------------------------------------------------- updates

    private void ShowUpdate()
    {
        var manifest = UpdateService.Available;
        UpdateBar.Visibility = manifest == null ? Visibility.Collapsed : Visibility.Visible;
        if (manifest == null) return;
        UpdateTitle.Text = $"MakeItColor {manifest.Version} is available (you have {Updater.CurrentVersionText})";
        UpdateNotes.Text = string.Join("  ·  ", manifest.Notes.Take(3));
        UpdateNowButton.Content = UpdateService.CanInstall(manifest) ? "Update now" : "Open the download page";
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Available is not { } manifest) return;
        if (_items.OfType<VideoItem>().Any(v => v.IsWorking))
        {
            MessageDialog.Show(this, "A video is still being coloured", "Update when it's done, so it isn't stopped halfway.", "OK", icon: DialogIcon.Info);
            return;
        }
        UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = UpdateSkipButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            await UpdateService.UpdateNowAsync(new Progress<double>(p => UpdateProgress.Value = p));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update now");
            MessageDialog.Show(this, "Couldn't update MakeItColor", ex.Message + "\n\nYou can download the new version from the website instead.",
                "Open the download page", close: "Close", icon: DialogIcon.Error);
            UpdateService.OpenInBrowser(string.IsNullOrWhiteSpace(manifest.Page) ? UpdateService.DownloadPageUrl : manifest.Page!);
        }
        finally
        {
            UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = UpdateSkipButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUpdateLater(object sender, RoutedEventArgs e) => UpdateService.Later();

    private void OnUpdateSkip(object sender, RoutedEventArgs e) => UpdateService.Skip();

    private sealed class Command : ICommand
    {
        private readonly Action _run;
        public Command(Action run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _run();
    }
}
