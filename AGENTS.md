# Omen Island — guide for another model

This file is the source of truth for how the product behaves. The UI strings are Italian. The code is English. Do not reintroduce behavior listed under "Do not change".

## What it is

A .NET 8 WPF overlay for HP OMEN and Victus laptops. It sits at the top center of the screen, independent of OmenMon and OMEN Gaming Hub. It does not install a kernel driver.

Target machine for this repo: HP Victus 16-e0xxx, board 88EC, Ryzen 5 5600H, RTX 3050, Windows 11, BIOS around F.27. Other HP OMEN/Victus boards are detected at startup, but do not retune this machine's profile bytes to the newer OMEN map.

## Solution

- `src/OmenIsland.App` — WPF island, settings, media, calendar, tray, startup.
- `src/OmenIsland.Hardware` — HP WMI transport, temperatures, fan/profile commands, thermal policy.
- `tests/OmenIsland.Tests` — protocol, layout, thermal policy, media/calendar.
- `OmenIsland.sln` — build this. TFM is `net8.0-windows10.0.19041.0`, x64, `UseWPF` and `UseWindowsForms`. Manifest is `asInvoker`. Do not switch it to `requireAdministrator`.

Settings file: `%LOCALAPPDATA%\OmenIsland\settings.json`.

## Island states

`IslandStateController` has three states. The window is transparent (`AllowsTransparency=True`).

- Idle: a thin bar on the top edge of the monitor.
- Compact: hover. Temperatures, RAM, profile, and the media row when something is playing.
- Expanded: click. Three columns: player, performance (fixed width 360), calendar. `Esc` or mouse-leave returns to idle. Clicking the expanded surface must not collapse it to compact.

Bottom corners of the non-idle surface are rounded by clipping `RootHost`. Do not put a `DropShadowEffect` on the transparent window; it paints square black corners.

Compact width and expanded height use hysteresis so text and the spectrum do not jitter the island by a few pixels.

## Accent color

The island shell follows the taskbar (black or light). Icons, sliders, and the compact spectrum use `IslandActiveBrush`.

That brush is the wallpaper accent, retinted by `AccentColor.ForSurface` so it stays readable on the shell. The accent is **not** `DwmGetColorizationColor`. On Windows 11 that API returns the gray glass colorization color. The real accent is the ABGR dword `HKCU\Software\Microsoft\Windows\DWM\AccentColor`, with fallback `Explorer\Accent\AccentColorMenu`. `IslandWindow.ApplyShellColor` paints the existing brush when it is not frozen, and also listens for `WM_DWMCOLORIZATIONCOLORCHANGED` and `UserPreferenceChanged`.

## Media

`NowPlayingService` reads the Windows media session (Spotify, YouTube, and other SMTC sources), with play/pause, next/previous, seek, shuffle, repeat, and process volume.

Compact spectrum: `Views/WaveformBars.cs`. It is a bar spectrum, not a scrolling waveform. 36 bars, log bands from 50 Hz to 12000 Hz, exclusive FFT bins, global loudness normalization, mute gate `0.0018`. Color is `IslandActiveBrush`. Toggle is `NowPlaying.IsVisualizerOpen` / `ShowVisualizer`.

There is no cover-art visualizer. `WaveformRibbon` and `ShowCoverVisualizer` were removed on purpose. Do not restore that file unless the user asks for a new visualizer.

## Calendar

Read-only Google Calendar in the expanded view. OAuth client data lives in `Services/Calendar/GoogleSignIn.Local.cs`, which is gitignored (template: `GoogleSignIn.Local.cs.example`). Without it the calendar stays disconnected. Do not print or commit the client secret. The signed-in account is the user's; do not invent a different one.

## Hardware

`HpHardwareController` talks to HP BIOS WMI. Command ids are in `HpWmiProtocol`. Victus 16-e legacy profile bytes:

- Eco `0x02` (label Eco)
- Default `0x00` (label Predefinita)
- Quiet `0x03` (label Silenzioso)
- Performance `0x01` (label Performance)

Newer OMEN firmware uses a different map (`0x50` / `0x30` / `0x31`). Do not send those to this Victus.

Fan UI is Auto or Max only. `FanControlMode.Manual` is legacy and is rewritten to Auto when settings load. Do not add a percent slider.

Do not poll WMI command `0x4C` (`GetThermalProfile`).

Temperatures: LibreHardwareMonitor for CPU and NVIDIA GPU. Ryzen `Tctl/Tdie` often stays at 0 even when elevated; do not install PawnIO or WinRing0. If the CPU sensor is missing or not positive, `HardwareTelemetryReader.ReadAcpiCelsius` uses the hottest `MSAcpi_ThermalZoneTemperature` zone (deciKelvin). Values must be greater than 0 and below 125. Firmware WMI `0x23` is a separate fallback and must still run if the fan query throws. ACPI needs elevation; unelevated reads are access denied.

`ThermalSafetyPolicy.Evaluate` does **not** force Max when the CPU or GPU is hot. High temperature returns `None`. Stale telemetry while the fan is not Auto returns `RestoreAutomatic`. The app also restores Auto on exit. `ForceMaximum` remains in the enum only so old call sites compile; do not wire it back up. A hot ACPI zone previously forced Max by itself.

The first firmware write needs an in-app consent. After that, `HardwareControlAccepted` persists. Elevation is separate from consent.

## Startup

One UAC prompt, then later boots should not ask again.

- Scheduled task `Omen Island` at the root of the task scheduler. `HighestAvailable`, interactive token, logon trigger, action is the current exe plus `--background`. `AllowDemandStart`, not stopped on battery, no execution time limit.
- HKCU Run value `Omen Island` is `"<exe>" --start-elevated`. If that process is not admin, it runs `schtasks /Run /TN "Omen Island"` and exits without taking the mutex.
- An elevated start with `StartWithWindows` rewrites the task to the exe that just launched.

Single-instance mutex: `Local\OmenIsland.SingleInstance`. A second instance signals settings and exits. Kill the old process before starting a new one, or the new one dies.

`taskkill` of the elevated exe often returns "Accesso negato". Follow it with an elevated `taskkill`, then `Start-Process -Verb RunAs`. Build to a fresh folder under `%LOCALAPPDATA%\OmenIsland\<Name>` because the running elevated exe locks its own directory. Do not `dotnet run` over the copy that is already running.

## Build

```powershell
dotnet build .\src\OmenIsland.App\OmenIsland.App.csproj -c Release -o "$env:LOCALAPPDATA\OmenIsland\<Name>"
dotnet test .\OmenIsland.sln
```

PowerShell 5 has no `&&`. Do not commit or push unless the user asks. Do not print Google secrets, and do not commit `settings.json`.
