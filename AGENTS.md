# Repository Instructions

- When starting user-visible changes after a release, set the application version to the next intended release version.
- Do not bump the version again for additional changes or testing iterations before that release. Application versions must correspond to published releases, not individual development changes.
- Keep all changes planned for the same unreleased version under one changelog heading in `CHANGELOG.md`.
- After implementing a user-visible app change, publish and deploy it to the local installation, then restart the running app so the user can test the result.
- Keep version values in `src/TerminalSessionKeeper/TerminalSessionKeeper.csproj` in sync: `Version` and `InformationalVersion` use `N.N.N`; `FileVersion` and `AssemblyVersion` use `N.N.N.0`.
- Do not include commit hashes, source revision IDs, or other unique build identifiers in the About window version.
- Put versions in window titles in parentheses, e.g. `About Terminal Session Keeper (v1.3.0)` or `Terminal Session Keeper Settings (v1.3.0)`, rather than after a dash.

## Installer and local deployment

- Build the Windows installer with `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'E:\projects\TerminalSessionKeeper\scripts\build-installer.ps1'"` on Windows. Requires .NET 8 SDK and Inno Setup 6.
- The output is `dist/TerminalSessionKeeper-Setup.exe`, with `dist/SHA256SUMS.txt`. GitHub releases ship these files instead of the standalone application EXE.
- Default to a current-user installation without elevation at `%LOCALAPPDATA%\Programs\Terminal Session Keeper`.
- The installed EXE is `%LOCALAPPDATA%\Programs\Terminal Session Keeper\TerminalSessionKeeper.exe`. For the local Windows account this is `C:\Users\Standfest\AppData\Local\Programs\Terminal Session Keeper\TerminalSessionKeeper.exe`.
- Deploy through the installer. Stop the running app first, run setup, and restart the installed EXE. Do not deploy to or restart the old source-tree `bin` copy.
- Setup and uninstall close the app through `WM_CLOSE` on `TerminalSessionKeeperListener`, then wait for the `TerminalSessionKeeper.Singleton` mutex to be released. Setup uses `PrepareToInstall` and restarts a previously running app after upgrade. Do not add an `AppMutex` directive that prompts before automatic shutdown.
- Preserve existing startup shortcuts by selecting the installer's `startup` task ("Start with Windows") when one already exists. Shortcuts must target the installed EXE.
- Keep user settings, logs, and snapshots in `%LOCALAPPDATA%\TerminalSessionKeeper`; upgrades and uninstall must preserve them.
- About (Diagnostics tab) must show the actual running EXE path from `Environment.ProcessPath`, not a hard-coded installation path.

## GitHub updates

- Default to checking immediately on startup and daily while running. Settings must support hourly, daily, weekly, calendar-monthly, and never. Never disables startup and scheduled checks while leaving manual checks available. Keep checks asynchronous and prevent overlapping checks or downloads.
- Offer Update/Later for a newer stable release with `TerminalSessionKeeper-Setup.exe`. Download only after Update is clicked. Verify the GitHub SHA-256 digest or published `SHA256SUMS.txt` before launching setup.
- Keep background failures quiet. Manual Check for updates lives on the Settings Updates tab, reporting results to the user.
- Run tests with `dotnet test tests/TerminalSessionKeeper.Tests/TerminalSessionKeeper.Tests.csproj -c Release`.

## UI and themes

- Every window and dialog is WPF styled by the shared AlexLV Windows UI kit, vendored byte-identically in `src/TerminalSessionKeeper/AlexLV.WindowsUi/`. The canonical copy is `C:\projects\AlexLV.WindowsUi` (see its README); change it there and copy it to every app. The tray icon, its menu and the message loop stay WinForms; `TrayMenuStyler` themes the menu and `WinFormsHosting.ShowModeless` opens modeless windows. Tray Keeper is the reference implementation.
- Every window derives from `ThemedWindow` (XAML root `ui:ThemedWindow`) so it never flashes light before its first themed frame. Use `MessageDialog`, never `MessageBox`.
- Keep `<InvariantGlobalization>false</InvariantGlobalization>`: WPF text layout creates real cultures, and invariant mode crashes the first `TextBox` it measures. A test guards it.
- The approved design is `design/ui-redesign.html`; update it before a visible redesign.
- Appearance → Theme offers System (default), Light and Dark, separate from the tab-colour options. System follows the Windows app mode live. A new theme previews at once; any close without Save restores the saved theme.
- Settings tabs (Snapshots, Restoring, Environment, Appearance, General, Updates, About, Diagnostics) must fit the fixed 780×700 window (eight tab headers need one row) without vertical scrolling; move overflowing content to another tab. `WindowTests` checks every tab in both themes.
- Accent: `#1D7347` light, `#78E0A0` dark (the icon's prompt green). Surfaces stay neutral.
- Version and release notes come only from `CHANGELOG.md` (embedded, parsed by `Changelog.cs`) and the csproj `<Version>`.

## About window layout

- Show the app icon before the app name. Paths live on the Diagnostics tab, selectable for copying; focus the footer Save button initially so diagnostic paths and release notes are not preselected.
- Include a "Copy details" button (Diagnostics tab) to copy diagnostic paths and release notes to the clipboard without requiring manual selection.

## Automated release identity

- Keep tag-driven GitHub Actions release publishing with `GITHUB_TOKEN`. GitHub showing `github-actions` as release author is expected and accepted by the owner. Do not add a personal token or manual publishing only to change this label. Commit authorship is separate.
- Order changelog and release notes by user impact. Use Main changes for the major features, followed by Other updates for smaller UI and wording refinements.
