using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using StealthDeck.Interop;
using StealthDeck.Services;

namespace StealthDeck;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://www.google.com/";
    private const string SearchUrlPrefix = "https://www.google.com/search?q=";
    private const double DefaultOpacityPercent = 95;
    private const double MinOpacityPercent = 25;
    private const double MaxOpacityPercent = 100;

    private static readonly string NotesPath = Path.Combine(App.AppDataDir, "notes.txt");
    private static readonly string WebViewDataDir = Path.Combine(App.AppDataDir, "WebView2Data");
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DispatcherTimer _saveTimer;

    private IntPtr _hwnd;
    private HotkeyService? _hotkeys;
    private TrayIconService? _tray;

    private bool _clickThrough;
    private bool _notesDirty;
    private bool _suppressNotesEvents;
    private bool _isExiting;
    private bool _cleanedUp;
    private bool _webViewInitStarted;
    private bool _webViewFailed;
    private bool _browserMode = true;

    private string? _shieldDetail;
    private TaskCompletionSource<bool>? _bannerTcs;
    private bool _bannerIsDialog;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(App.AppDataDir);

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNotes();
        };

        // Wire value/checked handlers after InitializeComponent so they never fire against half-built UI.
        OpacitySlider.Value = DefaultOpacityPercent;
        OpacityLabel.Text = $"{DefaultOpacityPercent:0}%";
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;

        BrowserModeButton.Checked += (_, _) => SetMode(browser: true);
        NotesModeButton.Checked += (_, _) => SetMode(browser: false);

        NotesBox.TextChanged += NotesBox_TextChanged;
        LoadNotes();
        SetMode(browser: true);

        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B);

        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += MainWindow_Closing;
        StateChanged += (_, _) =>
        {
            // A maximized WindowChrome window overhangs the monitor by the frame width; pad it back in.
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    // =====================================================================================
    //  Win32 shell integration
    // =====================================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_LAYERED must be paired with SetLayeredWindowAttributes before first show,
        // otherwise the window is invisible.
        ApplyExtendedStyles();
        ApplyOpacity(OpacitySlider.Value);
        ApplyRoundedCorners();
        UpdateShieldStatus(CaptureShield.Apply(_hwnd));

        _hotkeys = new HotkeyService(_hwnd);
        var failedHotkeys = new List<string>();
        foreach (Shortcut shortcut in BuildShortcuts())
        {
            if (!_hotkeys.TryRegister(shortcut.Modifiers, shortcut.Key, shortcut.Action))
            {
                failedHotkeys.Add(shortcut.Label);
            }
        }

        _tray = new TrayIconService(
            toggleVisibility: ToggleVisibility,
            toggleClickThrough: ToggleClickThrough,
            showShortcuts: ShowShortcutsHelp,
            // Deferred so the tray menu finishes its click handler before it is disposed.
            exit: () => Dispatcher.BeginInvoke(new Action(RequestExit)));

        if (failedHotkeys.Count > 0)
        {
            ShowInfoBanner($"{string.Join(", ", failedHotkeys)} could not be registered because another app already uses " +
                           "them. Use the StealthDeck tray icon menu instead.");
        }
    }

    // =====================================================================================
    //  Global shortcuts
    // =====================================================================================

    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
    // Ctrl+Alt+Shift is almost never claimed by other apps, so the extra shortcuts live there.
    private const ModifierKeys CtrlAltShift = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
    private const double OpacityStep = 10;
    private const double MoveStep = 40;
    private const double DefaultWidth = 980;
    private const double DefaultHeight = 640;

    private sealed record Shortcut(ModifierKeys Modifiers, Key Key, string Label, string Description, Action Action);

    private Shortcut[] BuildShortcuts() => new Shortcut[]
    {
        new(CtrlShift, Key.H, "Ctrl+Shift+H", "Show / hide the overlay", ToggleVisibility),
        new(CtrlShift, Key.T, "Ctrl+Shift+T", "Toggle click-through", ToggleClickThrough),
        new(CtrlAltShift, Key.M, "Ctrl+Alt+Shift+M", "Switch Browser / Notes", ToggleMode),
        new(CtrlAltShift, Key.L, "Ctrl+Alt+Shift+L", "Focus the address bar", FocusAddressBar),
        new(CtrlAltShift, Key.OemPlus, "Ctrl+Alt+Shift+=", "Opacity +10%", () => StepOpacity(+OpacityStep)),
        new(CtrlAltShift, Key.OemMinus, "Ctrl+Alt+Shift+-", "Opacity −10%", () => StepOpacity(-OpacityStep)),
        new(CtrlAltShift, Key.Left, "Ctrl+Alt+Shift+←", "Move window left", () => MoveBy(-MoveStep, 0)),
        new(CtrlAltShift, Key.Right, "Ctrl+Alt+Shift+→", "Move window right", () => MoveBy(MoveStep, 0)),
        new(CtrlAltShift, Key.Up, "Ctrl+Alt+Shift+↑", "Move window up", () => MoveBy(0, -MoveStep)),
        new(CtrlAltShift, Key.Down, "Ctrl+Alt+Shift+↓", "Move window down", () => MoveBy(0, MoveStep)),
        new(CtrlAltShift, Key.R, "Ctrl+Alt+Shift+R", "Reset size & position (rescues an off-screen window)", ResetWindowBounds),
        new(CtrlAltShift, Key.OemQuestion, "Ctrl+Alt+Shift+/", "Show this shortcut list", ShowShortcutsHelp),
    };

    public void ShowShortcutsHelp()
    {
        ShowOverlay();
        var lines = new StringBuilder("Keyboard shortcuts (work from any app):\n");
        foreach (Shortcut s in BuildShortcuts())
        {
            lines.Append('\n').Append(s.Label.PadRight(22)).Append(s.Description);
        }

        lines.Append("\n\nIn the browser: Alt+← / Alt+→ back/forward, F5 reload, Ctrl+F find, Ctrl+± zoom.");
        lines.Append("\nIn notes: Ctrl+Z undo, Ctrl+A select all.");
        ShowInfoBanner(lines.ToString());
        if (BannerText.Text == lines.ToString())
        {
            BannerText.FontFamily = new FontFamily("Consolas"); // keeps the columns aligned
        }
    }

    private void ToggleMode()
    {
        ShowOverlay();
        if (_browserMode)
        {
            NotesModeButton.IsChecked = true;
        }
        else
        {
            BrowserModeButton.IsChecked = true;
        }
    }

    private void FocusAddressBar()
    {
        ShowOverlay();
        BrowserModeButton.IsChecked = true;
        if (UrlBox.IsEnabled)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                UrlBox.Focus();
                Keyboard.Focus(UrlBox);
                UrlBox.SelectAll();
            }));
        }
    }

    private void StepOpacity(double delta) =>
        OpacitySlider.Value = Math.Clamp(OpacitySlider.Value + delta, MinOpacityPercent, MaxOpacityPercent);

    private void MoveBy(double dx, double dy)
    {
        if (!IsVisible || WindowState != WindowState.Normal)
        {
            return;
        }

        Left += dx;
        Top += dy;
    }

    private void ResetWindowBounds()
    {
        ShowOverlay();
        WindowState = WindowState.Normal;
        Rect work = SystemParameters.WorkArea; // primary monitor, excluding the taskbar
        Width = Math.Min(DefaultWidth, work.Width);
        Height = Math.Min(DefaultHeight, work.Height);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void ApplyExtendedStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        long current = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long updated = (current | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED) & ~NativeMethods.WS_EX_APPWINDOW;
        updated = _clickThrough
            ? updated | NativeMethods.WS_EX_TRANSPARENT
            : updated & ~NativeMethods.WS_EX_TRANSPARENT;

        if (updated == current)
        {
            return;
        }

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(updated));
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    private void ApplyOpacity(double percent)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        double clamped = Math.Clamp(percent, MinOpacityPercent, MaxOpacityPercent);
        byte alpha = (byte)Math.Round(clamped / 100.0 * 255.0);
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    private void ApplyRoundedCorners()
    {
        // Windows 11 only; harmlessly fails (non-zero HRESULT) on Windows 10.
        int preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void UpdateShieldStatus(ShieldResult result)
    {
        switch (result.Status)
        {
            case ShieldStatus.Excluded:
                _shieldDetail = null;
                ShieldWarningButton.Visibility = Visibility.Collapsed;
                break;

            case ShieldStatus.MonitorOnly:
                _shieldDetail = "WDA_EXCLUDEFROMCAPTURE is not supported on this Windows build (requires Windows 10 v2004 / " +
                                $"build 19041 or newer; Win32 error {result.Win32Error}). Fallback WDA_MONITOR is active: " +
                                "StealthDeck will appear as a solid black rectangle in screen shares and recordings.";
                ShieldWarningText.Text = "⚠ Shows as black box";
                ShieldWarningButton.Visibility = Visibility.Visible;
                break;

            default:
                _shieldDetail = $"SetWindowDisplayAffinity failed (Win32 error {result.Win32Error}). StealthDeck is currently " +
                                "VISIBLE to screen sharing, recording and screenshots.";
                ShieldWarningText.Text = "⚠ Visible to capture";
                ShieldWarningButton.Visibility = Visibility.Visible;
                break;
        }
    }

    // =====================================================================================
    //  Public commands (tray, hotkeys, single-instance activation)
    // =====================================================================================

    public void ToggleVisibility()
    {
        // A minimized tool window has no taskbar button, so treat "minimized" as hidden and restore it.
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            HideToTray();
        }
        else
        {
            ShowOverlay();
        }
    }

    public void ShowOverlay()
    {
        if (_cleanedUp)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Topmost = true;
        ApplyExtendedStyles(); // WPF may rewrite ex-styles on Show/Topmost; re-assert ours.
        Activate();
    }

    public void HideToTray()
    {
        SaveNotes();
        Hide();
    }

    public void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        ApplyExtendedStyles();
        ClickThroughBadge.Visibility = _clickThrough ? Visibility.Visible : Visibility.Collapsed;
        _tray?.SetClickThroughChecked(_clickThrough);
    }

    public void PrepareForExit() => _isExiting = true;

    public void RequestExit()
    {
        PrepareForExit();
        Cleanup();
        Application.Current.Shutdown();
    }

    /// <summary>Idempotent teardown: flush notes, unregister hotkeys, remove tray icon, release WebView2.</summary>
    public void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _saveTimer.Stop();
        SaveNotes();
        CompleteBanner(false);

        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;

        try
        {
            WebView.Dispose();
        }
        catch (InvalidOperationException)
        {
            // WebView2 already torn down with the window.
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Alt+F4 or any stray close request hides to the tray; only Exit (tray) really quits.
        if (!_isExiting)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    // =====================================================================================
    //  Top bar
    // =====================================================================================

    private void SetMode(bool browser)
    {
        _browserMode = browser;

        BrowserControls.Visibility = browser ? Visibility.Visible : Visibility.Collapsed;
        NotesControls.Visibility = browser ? Visibility.Collapsed : Visibility.Visible;

        BrowserHost.Visibility = browser ? Visibility.Visible : Visibility.Collapsed;
        WebView.Visibility = browser && !_webViewFailed ? Visibility.Visible : Visibility.Collapsed;
        NotesHost.Visibility = browser ? Visibility.Collapsed : Visibility.Visible;

        if (!browser)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                NotesBox.Focus();
                Keyboard.Focus(NotesBox);
            }));
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        OpacityLabel.Text = $"{e.NewValue:0}%";
        ApplyOpacity(e.NewValue);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestExit();

    private void OpacityControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // One wheel notch = 5%.
        StepOpacity(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void ShieldWarningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shieldDetail is not null)
        {
            ShowInfoBanner(_shieldDetail);
        }
    }

    // =====================================================================================
    //  Browser
    // =====================================================================================

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitStarted)
        {
            return;
        }

        _webViewInitStarted = true;

        try
        {
            Directory.CreateDirectory(WebViewDataDir);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: WebViewDataDir);

            await WebView.EnsureCoreWebView2Async(environment);

            ConfigureCoreWebView(WebView.CoreWebView2);
            WebView.CoreWebView2.Navigate(HomeUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowBrowserFallback("Microsoft Edge WebView2 Runtime is not installed.\n" +
                                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and restart StealthDeck.\n\n" +
                                "Notes mode still works.");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowBrowserFallback($"The embedded browser failed to start:\n{ex.Message}\n\nNotes mode still works.");
        }
    }

    private void ConfigureCoreWebView(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        // Native JS dialogs, the Chromium context menu and the download flyout are all separate
        // top-level windows outside our display-affinity tree. Replace or suppress them.
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;

        core.NewWindowRequested += OnNewWindowRequested;
        core.ScriptDialogOpening += OnScriptDialogOpening;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnProcessFailed;
        core.SourceChanged += (_, _) => SyncAddressBar(force: false);
        core.HistoryChanged += (_, _) => UpdateNavigationButtons();
        core.NavigationCompleted += (_, _) =>
        {
            UpdateNavigationButtons();
            SyncAddressBar(force: false);
        };

        UpdateNavigationButtons();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never let a popup/tab spawn an unshielded window: load it in place instead.
        e.Handled = true;

        if (string.IsNullOrWhiteSpace(e.Uri) || e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            WebView.CoreWebView2?.Navigate(e.Uri);
        }
        catch (ArgumentException)
        {
            // Malformed popup target; ignore.
        }
    }

    private async void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        CoreWebView2Deferral deferral = e.GetDeferral();
        try
        {
            bool isPrompt = e.Kind == CoreWebView2ScriptDialogKind.Prompt;
            bool isAlert = e.Kind == CoreWebView2ScriptDialogKind.Alert;
            string message = e.Kind == CoreWebView2ScriptDialogKind.Beforeunload
                ? "Leave this page? Changes you made may not be saved."
                : $"{SafeHost(e.Uri)} says:\n{e.Message}";

            bool accepted = await ShowBanner(
                message,
                okText: e.Kind == CoreWebView2ScriptDialogKind.Beforeunload ? "Leave" : "OK",
                cancelText: isAlert ? null : "Cancel",
                inputDefault: isPrompt ? e.DefaultText ?? string.Empty : null,
                isDialog: true);

            if (accepted)
            {
                if (isPrompt)
                {
                    e.ResultText = BannerInput.Text;
                }

                e.Accept();
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; // Suppress Edge's download flyout; report progress in the in-overlay banner.

        CoreWebView2DownloadOperation operation = e.DownloadOperation;
        string name = Path.GetFileName(e.ResultFilePath);
        ShowInfoBanner($"Downloading {name}…");

        operation.StateChanged += (_, _) =>
        {
            switch (operation.State)
            {
                case CoreWebView2DownloadState.Completed:
                    ShowInfoBanner($"Downloaded {name} → {operation.ResultFilePath}");
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    ShowInfoBanner($"Download of {name} failed ({operation.InterruptReason}).");
                    break;
            }
        };
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowInfoBanner("The page stopped responding and was reloaded.");
                WebView.CoreWebView2?.Reload();
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                ShowBrowserFallback("The embedded browser process exited unexpectedly.\nRestart StealthDeck to continue browsing.");
                break;
        }
    }

    private void ShowBrowserFallback(string message)
    {
        _webViewFailed = true;
        BrowserFallback.Text = message;
        BrowserFallback.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Collapsed;
        BackButton.IsEnabled = ForwardButton.IsEnabled = false;
        ReloadButton.IsEnabled = false;
        UrlBox.IsEnabled = false;
    }

    private void UpdateNavigationButtons()
    {
        CoreWebView2? core = WebView.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack ?? false;
        ForwardButton.IsEnabled = core?.CanGoForward ?? false;
    }

    private void SyncAddressBar(bool force)
    {
        CoreWebView2? core = WebView.CoreWebView2;
        if (core is not null && (force || !UrlBox.IsKeyboardFocusWithin))
        {
            UrlBox.Text = core.Source;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoBack == true)
        {
            WebView.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoForward == true)
        {
            WebView.CoreWebView2.GoForward();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => WebView.CoreWebView2?.Reload();

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                NavigateFromInput(UrlBox.Text);
                WebView.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                SyncAddressBar(force: true);
                WebView.Focus();
                e.Handled = true;
                break;
        }
    }

    private void UrlBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UrlBox.SelectAll();

    private void UrlBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // First click focuses and selects everything (browser-style) instead of placing the caret.
        if (!UrlBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            UrlBox.Focus();
        }
    }

    private void NavigateFromInput(string raw)
    {
        CoreWebView2? core = WebView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        string target = ResolveNavigationTarget(raw);
        try
        {
            core.Navigate(target);
        }
        catch (ArgumentException)
        {
            core.Navigate(SearchUrlPrefix + Uri.EscapeDataString(raw.Trim()));
        }
    }

    /// <summary>Turns address-bar input into a URL: explicit URLs pass through, bare hosts get https://, everything else is a Google search.</summary>
    internal static string ResolveNavigationTarget(string raw)
    {
        string input = raw.Trim();
        if (input.Length == 0)
        {
            return HomeUrl;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out Uri? absolute)
            && absolute.Scheme is "http" or "https" or "file" or "about")
        {
            return absolute.AbsoluteUri;
        }

        bool hasWhitespace = input.IndexOfAny(new[] { ' ', '\t' }) >= 0;
        bool isLocalhost = input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
        bool looksLikeHost = !hasWhitespace && (input.Contains('.') || isLocalhost);

        if (looksLikeHost
            && Uri.TryCreate((isLocalhost ? "http://" : "https://") + input, UriKind.Absolute, out Uri? guessed)
            && guessed.Host.Length > 0
            && (isLocalhost || guessed.Host.Contains('.')))
        {
            return guessed.AbsoluteUri;
        }

        return SearchUrlPrefix + Uri.EscapeDataString(input);
    }

    private static string SafeHost(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.Host.Length > 0 ? parsed.Host : "This page";

    // =====================================================================================
    //  In-overlay banner (dialogs, downloads, warnings)
    // =====================================================================================

    private Task<bool> ShowBanner(string message, string okText, string? cancelText, string? inputDefault, bool isDialog)
    {
        // A newer banner supersedes the old one; a superseded dialog resolves as "cancel".
        _bannerTcs?.TrySetResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bannerTcs = tcs;
        _bannerIsDialog = isDialog;

        BannerText.Text = message;
        BannerText.FontFamily = new FontFamily("Segoe UI");
        BannerOkButton.Content = okText;

        BannerCancelButton.Visibility = cancelText is null ? Visibility.Collapsed : Visibility.Visible;
        BannerCancelButton.Content = cancelText ?? "Cancel";

        BannerInput.Visibility = inputDefault is null ? Visibility.Collapsed : Visibility.Visible;
        BannerInput.Text = inputDefault ?? string.Empty;

        Banner.Visibility = Visibility.Visible;

        if (inputDefault is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                BannerInput.Focus();
                BannerInput.SelectAll();
            }));
        }

        return tcs.Task;
    }

    private void ShowInfoBanner(string message)
    {
        // Never stomp on a pending page dialog with an informational message.
        if (_bannerIsDialog && _bannerTcs is { Task.IsCompleted: false })
        {
            return;
        }

        _ = ShowBanner(message, okText: "Dismiss", cancelText: null, inputDefault: null, isDialog: false);
    }

    private void CompleteBanner(bool accepted)
    {
        Banner.Visibility = Visibility.Collapsed;
        TaskCompletionSource<bool>? tcs = _bannerTcs;
        _bannerTcs = null;
        _bannerIsDialog = false;
        tcs?.TrySetResult(accepted);

        if (_browserMode && !_webViewFailed)
        {
            WebView.Focus();
        }
    }

    private void BannerOk_Click(object sender, RoutedEventArgs e) => CompleteBanner(true);

    private void BannerCancel_Click(object sender, RoutedEventArgs e) => CompleteBanner(false);

    private void BannerInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CompleteBanner(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CompleteBanner(false);
            e.Handled = true;
        }
    }

    // =====================================================================================
    //  Notes (debounced, atomic auto-save)
    // =====================================================================================

    private enum NotesState { Saved, Pending, Error }

    private void LoadNotes()
    {
        try
        {
            if (File.Exists(NotesPath))
            {
                _suppressNotesEvents = true;
                NotesBox.Text = File.ReadAllText(NotesPath, Encoding.UTF8);
                NotesBox.CaretIndex = NotesBox.Text.Length;
            }

            SetNotesStatus(NotesState.Saved, "Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError(ex);
            SetNotesStatus(NotesState.Error, "Could not load notes");
        }
        finally
        {
            _suppressNotesEvents = false;
        }
    }

    private void NotesBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressNotesEvents)
        {
            return;
        }

        _notesDirty = true;
        SetNotesStatus(NotesState.Pending, "Editing…");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNotes()
    {
        if (!_notesDirty)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(App.AppDataDir);
            string tempPath = NotesPath + ".tmp";
            File.WriteAllText(tempPath, NotesBox.Text, Utf8NoBom);
            File.Move(tempPath, NotesPath, overwrite: true); // atomic replace: no torn file on crash
            _notesDirty = false;
            SetNotesStatus(NotesState.Saved, $"Saved {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError(ex);
            SetNotesStatus(NotesState.Error, "Save failed — retrying");
            if (!_cleanedUp)
            {
                _saveTimer.Stop();
                _saveTimer.Interval = TimeSpan.FromSeconds(3);
                _saveTimer.Start();
            }

            return;
        }

        _saveTimer.Interval = TimeSpan.FromMilliseconds(750);
    }

    private void ClearNotes_Click(object sender, RoutedEventArgs e)
    {
        if (NotesBox.Text.Length == 0)
        {
            return;
        }

        // Replace via selection so the clear lands on the undo stack (Ctrl+Z restores it).
        NotesBox.Focus();
        NotesBox.SelectAll();
        NotesBox.SelectedText = string.Empty;

        _saveTimer.Stop();
        SaveNotes();
    }

    private void SetNotesStatus(NotesState state, string text)
    {
        NotesStatusText.Text = text;
        NotesStatusDot.Fill = (Brush)FindResource(state switch
        {
            NotesState.Saved => "SuccessBrush",
            NotesState.Pending => "WarningBrush",
            _ => "DangerBrush",
        });
    }
}
