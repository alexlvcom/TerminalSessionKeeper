# Terminal Session Keeper — development

Needs the .NET 8 SDK and nothing else.

## Build, test and publish

```powershell
dotnet test TerminalSessionKeeper.sln -c Release

dotnet publish src\TerminalSessionKeeper\TerminalSessionKeeper.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

That produces the same single `publish\TerminalSessionKeeper.exe` the releases ship.

## Project layout

```
src/TerminalSessionKeeper/
  Agents/     per-agent session discovery (Claude, Codex, Antigravity, Junie) and the WSL probe
  Matching/   title tokenizing and the tab/title matcher
  Model/      snapshot records
  Native/     ConPTY input injection, PEB reads, process-tree walking
  Restore/    rebuilding a window from a snapshot
  Services/   paths, startup registration
  Settings/   settings model and store
  Snapshots/  capture and retention
  Terminal/   Windows Terminal interrogation, snapshot-agents.py for the WSL side
  Ui/         tray icon, Settings, Snapshots and About windows
  AppInfo.cs  the display name and the identifier name
tests/TerminalSessionKeeper.Tests/
```

Notable build settings, each there for a reason documented in the `.csproj`:
`UseWPF` for the UI Automation assembly, `IncludeNativeLibrariesForSelfExtract`
for the bundled SQLite provider, and `TreatWarningsAsErrors`.

## Versioning

The marketing version lives in two places that must agree:

- `<VersionPrefix>` and `<FileVersion>` in `src/TerminalSessionKeeper/TerminalSessionKeeper.csproj`
- the newest entry at the top of `Entries` in `Changelog.cs`

Add a changelog entry whenever behaviour changes and bump both. `AssemblyVersion`
is intentionally a wildcard with `Deterministic=false`, so the About window shows a
distinct build number per build.

## Naming

`AppInfo.cs` holds both forms and is the only place either is defined:

- `AppInfo.DisplayName` — **Terminal Session Keeper**, used in every user-visible
  string.
- `AppInfo.ShortName` — `TerminalSessionKeeper`, the identifier form: the assembly,
  the repository, the `%LocalAppData%` folder and the `Run`-key value, which must
  not change or an existing autostart registration would be orphaned.
