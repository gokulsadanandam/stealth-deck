using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using StealthDeck.Interop;

namespace StealthDeck.Services;

/// <summary>
/// System-wide hotkeys via RegisterHotKey, dispatched through the window's HwndSource hook.
/// Works while the window is hidden because the HWND stays alive.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // Application-defined hotkey ids must be in the range 0x0000-0xBFFF.
    private const int FirstId = 0xB000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = FirstId;
    private bool _disposed;

    public HotkeyService(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        }

        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd)
                  ?? throw new InvalidOperationException("The window handle is not backed by an HwndSource.");
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a global hotkey. Returns false if another application already owns the combination.</summary>
    public bool TryRegister(ModifierKeys modifiers, Key key, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);

        uint fsModifiers = NativeMethods.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) fsModifiers |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) fsModifiers |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) fsModifiers |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) fsModifiers |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        int id = _nextId++;

        if (!NativeMethods.RegisterHotKey(_hwnd, id, fsModifiers, vk))
        {
            return false;
        }

        _handlers[id] = callback;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out Action? callback))
        {
            callback();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (int id in _handlers.Keys)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _handlers.Clear();

        try
        {
            _source.RemoveHook(WndProc);
        }
        catch (ObjectDisposedException)
        {
            // Window already torn down; nothing left to unhook.
        }
    }
}
