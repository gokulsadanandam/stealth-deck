using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using StealthDeck.Interop;

namespace StealthDeck.Services;

internal enum ShieldStatus
{
    /// <summary>WDA_EXCLUDEFROMCAPTURE applied: window is omitted from capture, content beneath is recorded.</summary>
    Excluded,
    /// <summary>Only WDA_MONITOR could be applied: window shows as a black box in captures.</summary>
    MonitorOnly,
    /// <summary>No display affinity could be applied: window is fully visible to capture.</summary>
    Unavailable,
}

internal readonly record struct ShieldResult(ShieldStatus Status, int Win32Error);

/// <summary>
/// Applies display affinity to top-level windows and to WPF popups (tooltips, context menus),
/// which are separate top-level HWNDs and would otherwise leak into screen captures.
/// </summary>
internal static class CaptureShield
{
    private static bool _popupShieldRegistered;

    public static ShieldResult Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return new ShieldResult(ShieldStatus.Unavailable, 0);
        }

        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE)
            && NativeMethods.GetWindowDisplayAffinity(hwnd, out uint applied)
            && applied == NativeMethods.WDA_EXCLUDEFROMCAPTURE)
        {
            return new ShieldResult(ShieldStatus.Excluded, 0);
        }

        int error = Marshal.GetLastWin32Error();

        // Pre-2004 builds reject 0x11; WDA_MONITOR at least blacks the window out of captures.
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR))
        {
            return new ShieldResult(ShieldStatus.MonitorOnly, error);
        }

        return new ShieldResult(ShieldStatus.Unavailable, Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Registers class-level handlers so every WPF ContextMenu / ToolTip popup HWND gets
    /// the same capture exclusion as the main window the moment it opens.
    /// </summary>
    public static void RegisterPopupShielding()
    {
        if (_popupShieldRegistered)
        {
            return;
        }

        _popupShieldRegistered = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
    }

    private static void OnPopupOpened(object sender, RoutedEventArgs e)
    {
        if (sender is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source && source.Handle != IntPtr.Zero)
        {
            Apply(source.Handle);
        }
    }
}
