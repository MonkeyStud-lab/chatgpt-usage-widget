# ChatGPT Usage Widget for Windows

A slim, always-on-top C#/.NET 8 WPF widget for the ChatGPT Work/Codex allowance. Version 1.2 uses a 344 × 44 logical-pixel strip that alternates five-hour and weekly usage with fade transitions, with reduced background work. Original implementation; no third-party widget code was copied. This is an independent app, not an official OpenAI product.

![Compact usage strip with sample values](assets/preview.png)

Download the portable Windows build from this repository's Releases page.

## Start

1. Extract `UsageWidget-win-x64.zip` into a permanent folder.
2. Run `UsageWidget.exe`. The portable self-contained package includes .NET; no .NET installation is needed.
3. Keep the official Codex desktop app or CLI installed and signed in with ChatGPT. The widget auto-discovers `codex.exe` from PATH or the Codex desktop installation. If needed, use Settings → Browse to select it and Settings → Sign in to Codex. The widget never reads or copies authentication tokens itself.

If Windows asks about this executable, it is a locally built, unsigned app. Source is provided for inspection and rebuilding. No administrator rights are required for normal operation.

## Controls

- Drag the ChatGPT title or empty border to reposition. Position saves after dragging. By default the strip sits over a blank area of the primary taskbar; horizontal position is adjustable by dragging. Disable the taskbar placement setting to float it anywhere.
- The `−` button hides the widget to the tray. Double-click the tray icon to show it.
- The tray menu provides Show, Refresh, Open Usage, Settings, and Exit. Closing hides to the tray; Exit stops the widget and its polling.
- Double-click the usage row, or choose Open Usage from the tray menu, to open `https://chatgpt.com/#settings/Usage`. This browser route is a best-effort shortcut: if ChatGPT opens its home page, select Settings → Usage manually.
- Settings offers dark/light/system theme, 60/120/300/600-second refresh, notifications, a Codex executable override, taskbar placement, and a configurable rotation interval (default eight seconds). Each transition fades out for 250 ms, switches window, then fades in for 250 ms. Hover over the strip for refresh/error details. The screenshot's plan label, scope, remaining percentage, countdown, health label, and progress bar are retained in compact form.
- **Windows autostart is disabled and its setting has been removed.** The widget removes its old Run registration if one exists. Launch the widget manually once per Windows session. A small hidden watcher checks every two seconds; the strip and tray icon appear only while a `ChatGPT.exe` desktop window is open, including when minimized. Background renderer processes without a desktop window do not count. This installed OpenAI desktop app uses that executable name.
- Closing the ChatGPT window hides the strip and tray and stops polling; reopening it brings the strip back and refreshes immediately. The hidden watcher remains for the current session. If you explicitly hide the strip with `−`, it stays hidden until restored from the tray or the ChatGPT window closes and reopens. Choose Exit to stop the watcher. There is no automatic launch after a Windows restart.

## Taskbar integration

This is an overlay window, not a native taskbar extension. [Microsoft documents that Windows 11 apps cannot customize areas of the taskbar](https://www.microsoft.com/en-us/windows/windows-11-specifications). The strip fits a standard 48-pixel taskbar at normal Windows scaling. It does not reserve space or rearrange taskbar icons: drag it into an unused area. A smaller taskbar, unusual scaling, auto-hide, vertical taskbars, or full-screen apps may work better with floating mode. No Explorer patching or process injection is used.

The progress bars show **remaining**, not consumed usage. Green means above 25%; yellow means above 10% through 25%; red means 10% or less. The tray number is the lower of the available, unexpired five-hour/weekly percentages, rounded down. `?` means unavailable or stale; `!` means the backend reports a restriction. The tooltip explains the number.

Notifications use Windows tray balloons at 25%, 10%, and 5% remaining per window. They are deduplicated per account, reset timestamp, window, and threshold across restarts. A first read already below several thresholds produces one combined notification for the lowest threshold. Windows notification settings can suppress balloons. Alerts require an account identifier and reset timestamp from the server.

## Data source and accuracy

Official interface: [Codex app-server documentation](https://learn.chatgpt.com/docs/app-server).

The widget starts a hidden `codex app-server --listen stdio://` child for each refresh, performs `initialize` / `initialized`, and reads `account/rateLimits/read`. It makes no inference calls, does not spend API credits, and does not request paid quota resets. Each child is closed after the read; a 30-second timeout bounds network failures. Server diagnostics are drained without saving them.

The `codex` entry in `rateLimitsByLimitId` takes priority over the legacy single-bucket response. Only actual 300-minute and 10,080-minute windows are labeled five-hour and weekly. Missing percentages or unsupported durations display unavailable rather than fabricated values. Remaining = clamp(100 − usedPercent, 0, 100). Plan comes from the returned quota snapshot; it is omitted when unavailable.

**These figures represent the Work/Codex quota returned by your account, not every ChatGPT model-specific chat limit.** Other model buckets and credit balances are not displayed. The official local interface is versioned and can change; incompatibility displays an unavailable status rather than scraping the website or guessing.

Countdowns display days/hours/minutes and update every 15 seconds or on a window switch. Once a reset passes, the bar becomes neutral and says “Awaiting updated quota” until a successful server refresh. Failed refreshes retain the last snapshot with an explicit stale/error label and a neutral tray icon. The widget cannot guarantee continuously current numbers between refreshes.

## Resource optimizations

- The active ChatGPT window handle is cached and validated through native window checks. Full process discovery happens only when that handle becomes invalid or hidden, rather than on every two-second check. All discovered process wrappers are disposed.
- Theme brushes are allocated once, frozen, and reused. System theme changes arrive through Windows preference events; no registry read or full theme repaint occurs every second.
- Only the displayed usage row is updated. The hidden row is refreshed immediately when a fade switches to it.
- Countdown and rotation timers stop while the strip is hidden. Usage refreshes stop while ChatGPT is closed. The small presence watcher remains to detect reopening.
- Completed animation clocks are detached, and tray tooltip/icon updates are skipped when unchanged.
- Routine successful usage reads no longer rewrite settings; settings save when preferences/position/alert deduplication actually change.

The widget still uses WPF and .NET, with a nonzero baseline memory footprint. Each usage refresh starts a short-lived official Codex helper; its transient resources are additional to the widget's idle figures. A helper is closed after each read, and no permanent second Codex server is maintained. The default refresh is still 120 seconds; 300 or 600 seconds can reduce refresh work in exchange for older usage values. No inference/API-credit requests are made.

Measurements on this PC (16 logical processors): two 40-second visible-widget samples averaged 178.3 MiB resident / 108.3 MiB private committed memory and 0.053% total-machine CPU before the update, versus 131.5 MiB resident / 65.4 MiB private committed memory and 0.031% total-machine CPU after it. The old instance had been running longer; the optimized sample began 15 seconds after restart. These are observations, not guaranteed limits or a controlled attribution of every saved byte to code changes. Memory includes shared framework/rendering pages; private committed memory is a different measure and should not be added to resident memory.

Three successful read-only probes completed in 0.63–0.77 seconds. Sampling the probes' direct child processes observed helper working sets peaking at 113–139 MiB; short unsampled peaks or descendants could be higher. These transient helper figures are additional to the widget and separate from the Codex desktop app's own processes. The tests include the normal eight-second fade rotation; no model requests were sent. GPU usage was not measured.

## Local storage and privacy

Preferences, position, and alert deduplication keys live at `%LOCALAPPDATA%\CustomUsageWidget\settings.json`. Alert keys contain the backend's opaque account identifier; no email, tokens, passwords, or full server responses are stored. Credentials remain managed by official Codex. Network requests are made by that local Codex process to OpenAI's services.

The widget removes only its legacy `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CustomUsageWidget` value and never registers itself for Windows startup. To remove the app, choose Exit, delete its folder, and optionally delete the settings folder.

## Build

Use a supported .NET 8 SDK on Windows:

```powershell
dotnet build UsageWidget.csproj -c Release
dotnet publish UsageWidget.csproj -c Release -r win-x64 --self-contained true -o publish
```

The folder-based portable distribution avoids executable self-extraction and is compatible with WPF. Windows x64 is the tested target. Other architectures require a corresponding publish RID and Codex executable.

## Validation

```powershell
./bin/Release/net8.0-windows/UsageWidget.exe --self-test tests.txt
./bin/Release/net8.0-windows/UsageWidget.exe --probe probe.txt
./bin/Release/net8.0-windows/UsageWidget.exe --ui-check preview.png
./bin/Release/net8.0-windows/UsageWidget.exe --ui-check --weekly weekly-preview.png
./bin/Release/net8.0-windows/UsageWidget.exe --preview --animation-check animation.txt
```

`--self-test` covers 18 usage, threshold, and app-presence checks: response parsing, multi-bucket precedence, missing values, quota restrictions, percentage bounds, duration labeling, threshold boundaries, expiry, skipped thresholds, deduplication, reset rearming, disabled alerts, hiding while closed, displaying on launch, manual hiding, restoring, and rearming on relaunch. `--probe` reads live usage and writes only plan/percentages/restriction state; it does not write account identifiers or credentials. `--ui-check` renders sample values and exits; `--preview` opens the sample widget without polling or a tray icon. `--animation-check` verifies completion of the actual WPF fade/switch/fade transition and exits. In normal operation, only one instance runs.

Build and tests passed, both compact window layouts were rendered and visually inspected, and a live Plus account read returned both requested windows. Real threshold balloons and the browser's handling of the Settings route need user-environment confirmation; notifications were not enabled during validation.

## License

Original widget source is MIT licensed; see LICENSE. Codex is a separately installed dependency and is not redistributed. The self-contained package includes Microsoft's .NET runtime with its accompanying license/third-party notices. OpenAI and ChatGPT names belong to their respective owners.
