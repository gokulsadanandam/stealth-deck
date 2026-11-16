# StealthDeck

Always-on-top WPF overlay (WebView2 browser + Quick Notes) excluded from screen capture via
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.

## Requirements
- Windows 10 v2004 (build 19041) or newer. Windows 11 recommended.
- .NET 8 SDK: `winget install Microsoft.DotNet.SDK.8`
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).

## Build & run
```powershell
cd StealthDeck
dotnet restore
dotnet build -c Release
dotnet run -c Release
# Self-contained single exe (optional):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Hotkeys
| Keys | Action |
|---|---|
| Ctrl+Shift+H | Show / hide overlay |
| Ctrl+Shift+T | Toggle click-through (mouse passes to apps beneath) |
| Ctrl+Alt+Shift+M | Switch Browser / Notes |
| Ctrl+Alt+Shift+L | Focus the address bar |
| Ctrl+Alt+Shift+= / - | Opacity ±10% |
| Ctrl+Alt+Shift+Arrows | Move the window 40px |
| Ctrl+Alt+Shift+R | Reset size and position (rescues an off-screen window) |
| Ctrl+Alt+Shift+/ | Show the shortcut list (also in the tray menu) |

Exit only from the tray icon menu. Alt+F4 and the `—` button hide to the tray.

## Data
`%LocalAppData%\StealthDeck\` holds `notes.txt`, `WebView2Data\` (cookies/sessions) and `error.log`.

## Verifying capture exclusion
1. **Snipping Tool**: Win+Shift+S, capture full screen. The overlay must be absent; the windows behind it appear.
2. **OBS**: add a *Display Capture* source (and separately a *Window Capture* of an app beneath). The overlay must not appear in the preview.
3. **Teams / Zoom / Meet**: share your entire screen and check the self-preview or a second device.
4. If the top bar shows an amber `⚠` badge, the shield failed. Click it for details.

## Known limits
- Exclusion covers OS capture APIs only. A phone camera pointed at the screen still sees it.
- Chromium `<select>` dropdowns, autofill suggestions, file pickers and print dialogs are separate OS windows and **can** appear in captures. JS dialogs, downloads, popups/new tabs and the right-click menu are contained or disabled.
- The tray icon is part of the taskbar and visible in a full-screen share unless it is in the overflow (`^`) area.
