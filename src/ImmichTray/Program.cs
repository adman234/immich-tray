using ImmichTray.CloudFiles;
using ImmichTray.Settings;
using ImmichTray.UI;

namespace ImmichTray;

internal static class Program
{
    private const string MutexName = @"Local\ImmichTray.SingleInstance";
    private const string ShowEventName = @"Local\ImmichTray.Show";
    private const string QuitEventName = @"Local\ImmichTray.Quit";

    [STAThread]
    private static int Main(string[] args)
    {
        // Let this process see placeholders as what they are (see Native.RtlSetProcessPlaceholderCompatibilityMode).
        try { Native.RtlSetProcessPlaceholderCompatibilityMode(Native.PHCM_EXPOSE_PLACEHOLDERS); } catch { }

        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            return Uninstall();
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
        {
            SignalQuitAndWait();
            return 0;
        }

        using var mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that copy to show itself. (When Explorer starts us for
            // thumbnails while we are already running, COM talks to the running copy instead.)
            try
            {
                if (!args.Any(a => a.Contains("Embedding", StringComparison.OrdinalIgnoreCase)) &&
                    EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                {
                    show.Set();
                    show.Dispose();
                }
            }
            catch { }
            return 0;
        }

        Log.Info($"Starting {AppInfo.ProductName} {AppInfo.Version} ({string.Join(' ', args)})");
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn("Unobserved task exception", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        using var app = new TrayApp();

        var listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { showEvent, quitEvent };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                if (which == 0) app.OnSecondInstance();
                else
                {
                    Log.Info("Quit requested by another process");
                    SynchronizationContextHolder.Post(app.Quit);
                    return;
                }
            }
        })
        { IsBackground = true, Name = "InstanceListener" };
        SynchronizationContextHolder.Capture();
        listener.Start();

        Application.Run(app);
        Log.Info("Exited");
        return 0;
    }

    /// <summary>Asks a running copy to quit and waits for it to go away.</summary>
    private static void SignalQuitAndWait()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            {
                quit.Set();
                quit.Dispose();
                for (var i = 0; i < 50; i++)
                {
                    if (!Mutex.TryOpenExisting(MutexName, out var m)) break;
                    m.Dispose();
                    Thread.Sleep(200);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not stop the running app", ex);
        }
    }

    /// <summary>Run by the uninstaller: remove the Explorer folder and everything we registered.</summary>
    private static int Uninstall()
    {
        Log.Info("Uninstall cleanup starting");
        SignalQuitAndWait();
        try
        {
            var path = SyncRootRegistrar.GetRegisteredPath();
            if (path != null) MountManager.RemoveFolder(path);
            else SyncRootRegistrar.Unregister();
        }
        catch (Exception ex)
        {
            Log.Error("Removing the Immich folder failed", ex);
        }
        SyncRootRegistrar.UnregisterThumbnailClass();
        try { AutoStart.Set(false); } catch { }
        Log.Info("Uninstall cleanup done");
        return 0;
    }
}

/// <summary>Lets background threads run code on the UI thread.</summary>
internal static class SynchronizationContextHolder
{
    private static SynchronizationContext? _ctx;
    public static void Capture() => _ctx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    public static void Post(Action action) => _ctx?.Post(_ => action(), null);
}
