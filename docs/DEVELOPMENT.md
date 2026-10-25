# Terminal Session Keeper — development

Needs the .NET 8 SDK and nothing else.

## Build, test and publish

```powershell
dotnet test TerminalSessionKeeper.sln -c Release

dotnet publish src\TerminalSessionKeeper\TerminalSessionKeeper.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

That produces the single `publish\TerminalSessionKeeper.exe` that the installer packages.

Releases ship the Inno Setup installer: `scripts\build-installer.ps1` (needs Inno Setup 6)
builds `dist\TerminalSessionKeeper-Setup.exe` and `dist\SHA256SUMS.txt` from `installer\TerminalSessionKeeper.iss`.
The wizard logo bitmaps in `installer\images` are generated from the app icon by
`scripts\generate-installer-images.ps1`; rerun it after changing `Resources\app.ico`.

## Project layout

```
src/TerminalSessionKeeper/
  Agents/     per-agent session discovery (Claude, Codex, Antigravity, Junie) and the WSL probe
  Matching/   title tokenizing and the tab/title matcher
  Model/      snapshot records
  Native/     ConPTY input injection, keystrokes, PEB reads, process-tree walking
  Restore/    rebuilding a window from a snapshot, and colouring its tabs
  Services/   paths, startup registration
  Settings/   settings model and store
  Snapshots/  capture, retention, and the tab-colour history
  Terminal/   Windows Terminal interrogation, snapshot-agents.py for the WSL side
  Ui/         tray icon (WinForms) and the WPF Settings, Snapshots, Update and Change History windows
  AlexLV.WindowsUi/  vendored copy of the shared theme kit (do not edit here)
  AppInfo.cs  the display name and the identifier name
tests/TerminalSessionKeeper.Tests/
```

Notable build settings, each there for a reason documented in the `.csproj`:
`UseWPF` for the UI Automation assembly, `IncludeNativeLibrariesForSelfExtract`
for the bundled SQLite provider, and `TreatWarningsAsErrors`.

## Versioning

The version lives in `src/TerminalSessionKeeper/TerminalSessionKeeper.csproj`
(`Version`, `InformationalVersion`, `FileVersion`, `AssemblyVersion`), and the release
notes live only in `CHANGELOG.md`, which is embedded and parsed by `Changelog.cs`. The
newest `## [N.N.N] - date` heading must match `<Version>`; `ChangelogTests` fails
otherwise. Up to 1.3.2 a second, hand-maintained history in `Changelog.cs` also decided
the version.

## UI

Every window is WPF on the shared AlexLV Windows UI kit in `Ui/../AlexLV.WindowsUi/`
(canonical copy `C:\projects\AlexLV.WindowsUi`); the tray icon and message loop stay
WinForms. Windows derive from `ThemedWindow`. `InvariantGlobalization` must stay off,
because WPF text layout needs culture data. `WindowTests` opens the real windows off
screen in both themes and fails when a Settings tab would scroll.

## Naming

`AppInfo.cs` holds both forms and is the only place either is defined:

- `AppInfo.DisplayName` — **Terminal Session Keeper**, used in every user-visible
  string.
- `AppInfo.ShortName` — `TerminalSessionKeeper`, the identifier form: the assembly,
  the repository, the `%LocalAppData%` folder and the `Run`-key value, which must
  not change or an existing autostart registration would be orphaned.
