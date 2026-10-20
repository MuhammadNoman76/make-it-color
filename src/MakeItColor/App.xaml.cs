using System.Windows;
using System.Windows.Threading;
using MakeItColor.Core;
using MakeItColor.Themes;
using MakeItColor.Views;

namespace MakeItColor;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI thread");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task");
            args.SetObserved();
        };

        // Tests and scripts: colour a file (or just load the model) without a window, write a report, exit.
        if (Headless.Handles(e.Args))
        {
            Shutdown(await Headless.RunAsync(e.Args));
            return;
        }

        Settings.Load();
        ThemeManager.Initialize(Settings.Current.Theme);

        // Screenshots for the website and the docs: render the window off-screen in a given state, exit.
        if (Capture.Handles(e.Args))
        {
            Shutdown(await Capture.RunAsync(e.Args));
            return;
        }

        PhotoPrep.CleanTemp();
        var window = new MainWindow();
        MainWindow = window;
        window.Closed += (_, _) =>
        {
            PhotoPrep.CleanTemp(own: true);
            Shutdown();
        };
        window.Show();
        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (files.Count > 0) _ = window.Dispatcher.BeginInvoke(() => window.AddFiles(files), DispatcherPriority.Background);
    }
}
