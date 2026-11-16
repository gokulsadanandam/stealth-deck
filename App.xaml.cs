using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using StealthDeck.Services;

namespace StealthDeck;

public partial class App : Application
{
    private const string MutexName = @"Local\StealthDeck.SingleInstance.{8F3C2A51-6D4E-4B7A-9C1D-2E5F7A8B9C0D}";
    private const string ActivateEventName = @"Local\StealthDeck.Activate.{8F3C2A51-6D4E-4B7A-9C1D-2E5F7A8B9C0D}";

    internal static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StealthDeck");

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Create (or open) the activation event first so a second instance can always signal
        // the first one, with no window-finding or race against the first instance's startup.
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            _activateEvent.Set();      // Ask the running overlay to show + focus itself.
            _activateEvent.Dispose();
            _activateEvent = null;
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        _ownsMutex = true;
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogError(ex);
            }
        };

        CaptureShield.RegisterPopupShielding();

        _window = new MainWindow();
        MainWindow = _window;

        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.InvokeAsync(() => _window?.ShowOverlay()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _window?.PrepareForExit();
        _window?.Cleanup();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        _window?.Cleanup();

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned by this thread anymore; disposing below releases the handle.
                }
            }

            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never pop a MessageBox: it would be a separate, capturable top-level window.
        LogError(e.Exception);
        e.Handled = true;
    }

    internal static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.AppendAllText(
                Path.Combine(AppDataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
