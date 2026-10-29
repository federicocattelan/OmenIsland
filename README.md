# Omen Island

Omen Island is a lightweight Windows overlay for HP OMEN and Victus laptops.
It shows CPU/GPU temperatures and fan state, and can switch thermal profiles
or fan modes directly through HP's firmware WMI interface.

The project is independent from OmenMon and OMEN Gaming Hub. No executable,
library, service, or source code from either application is included.

## Current target

- Windows 10/11 x64
- HP Victus 16-e0xxx, including board 88EC (other HP OMEN and Victus models are detected at startup)

Fan speed on this generation is read as RPM through HP WMI command `0x11`.
Newer level-based firmware is detected automatically and used when it responds.
Profile values for Victus are Balanced `0x00`, Performance `0x01`, and Quiet `0x03`.
- .NET 8 Desktop

## Controls

- Search for **Omen Island** in the Start menu to open settings.
- Choose whether it starts with Windows. Automatic start shows only the top bar.
- Hover the bar for temperatures, RAM use, and the active profile.
- Click it for Eco, Predefinita, Silenzioso, Performance, and Auto/Max fans.
- Press `Esc` or click elsewhere to collapse it.

Firmware writes require administrator privileges. Without elevation the app
continues in read-only mode. The first write also requires an explicit
confirmation inside the app.

The fan control exposed in the island is Auto or Max. Omen Island restores
automatic fan control on exit and suspend, and restores it if telemetry goes
stale. A high temperature does not switch the fans to Max by itself.

## Build

```powershell
dotnet restore
dotnet build OmenIsland.sln
dotnet test OmenIsland.sln
dotnet run --project .\src\OmenIsland.App\OmenIsland.App.csproj
```

Run the built app as administrator to enable firmware controls.

## Architecture

- `OmenIsland.App`: WPF overlay, state machine, media, calendar, settings, and tray icon.
- `OmenIsland.Hardware`: clean-room HP WMI transport, telemetry, and safeguards.
- `OmenIsland.Tests`: protocol, layout, state, mapping, and thermal-policy tests.

`AGENTS.md` is the behavioral guide for further changes: states, accent color,
fans, temperatures, startup, and what must stay as it is.
