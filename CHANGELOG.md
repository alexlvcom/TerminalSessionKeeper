# Changelog

All notable changes to Terminal Session Keeper are documented here. Format follows Keep a Changelog; versions follow Semantic Versioning.

## [1.4.0] - 2026-10-20

### Main changes
- New modern look: Settings, Snapshots, the update dialog, change history, every message and the tray menu are redrawn with the shared AlexLV Windows design, in light and dark.
- New Appearance → Theme setting: System (default) follows the Windows light or dark mode and switches with it; Light and Dark fix the look. A choice previews at once; Save keeps it, and Cancel, Esc or closing Settings puts the saved theme back.
- Settings fits every tab without scrolling. Diagnostics (the executable, settings, snapshots and logs paths, and Copy details) moved out of About into a tab of its own.

### Other updates
- A taskbar pin made from an older copy of the app now opens the installed version instead.
- Check for updates in Settings → Updates reports its result in the status card: "You're running the latest version (v1.4.0)" with the check time, or why the check failed. No message box opens unless a newer version is available.
- Settings → Updates opens with what the last check actually found: a newer version with an Update button, the latest-version message, or the failure and its reason.
- Setup and uninstall now close a running copy cleanly. Before, the hidden window they ask to close never existed, so setup force-closed the app after a delay.
- The snapshot interval and the number of snapshots kept are checked on Save: a value outside 1–1440 minutes or 1–500 snapshots is reported beside the field instead of being changed silently.
- The footer shows how many changes are not saved yet.
- The update dialog shows download progress and offers Try again after a timeout or failure.
- The version shown in the title bar, About and the update check now comes from the build itself, and About shows the real build date, so they can never disagree with the release notes.

## [1.3.2] - 2026-10-20

### Main changes
- Settings now saves every preference, including the update check frequency, only when you click Save. Cancel, Esc, or closing the window discards all unsaved changes, so Cancel always undoes a frequency change.
- Save reports an error and keeps Settings open with your edits when settings.json cannot be written, instead of closing as if it had worked.

### Other updates
- Settings are written through a temporary file so a failed save never leaves a half-written settings.json.
- Saving Settings keeps the last update check time recorded while the window was open.

## [1.3.1] - 2026-10-19

- Start the update download timeout when Update is clicked. Report timeouts and allow retrying instead of leaving the download dialog stuck.

## [1.3.0] - 2026-10-17

### Main changes
- Added Windows installer (`TerminalSessionKeeper-Setup.exe`) installing per-user to `%LOCALAPPDATA%\Programs\Terminal Session Keeper` with automatic background upgrade and uninstall shutdown.
- Added in-app update checks against GitHub releases with configurable check frequency (Hourly, Daily, Weekly, Monthly, Never) and SHA-256 verification.
- Added graceful application shutdown via `TerminalSessionKeeperListener` NativeWindow on `WM_CLOSE`, safely releasing tray icons and mutexes during upgrades.

### Other updates
- Redesigned About dialog showing running executable path, data and log directories, current release notes, and a Copy details button.
- Cleaned up legacy Start menu shortcuts (`TerminalSessionKeeper.lnk`) automatically during setup.
- Standardized project metadata and fixed release versioning across assembly and installer manifests.

## [1.2.3] - 2026-10-06
- Snapshots window: the list fits each entry without dragging and shows how many tabs each snapshot has.
- Snapshots window: double-click (or Enter) a snapshot to restore it.

## [1.2.2] - 2026-09-24
- Made Settings taller so tab content and controls fit without being clipped.
- Capture PowerShell tab directories from the prompt so restored tabs reopen in the right folder and match their titles.
- Preserve tab order when reliable matches align, so identical stale shell directories do not steal each other's titles.
- Pass a restore marker to PowerShell tabs so startup hooks can preserve each saved starting directory.
- Keep terminal prompt colours when the tray app inherited a non-colour automation environment.
- Find Windows agents through shell wrappers and use a running Codex process's exact resume ID.

## [1.2.1] - 2026-09-23
- Fixed Settings not being saved. Closing the dialog cleared its internal reference before the saved values were applied, so the app threw an error and reopened with defaults. Environment variables and the WSL cd setting now persist.

## [1.2.0] - 2026-09-23
- New Settings tab, Environment: custom environment variables for restored WSL tabs, one NAME=value per line. They are added only to the tabs a restore opens, passed into WSL through WSLENV; tabs you open yourself are not affected.
- New setting, 'Type a cd into each restored WSL tab', on by default. It puts a tab back in its saved folder in case the shell changed directory while starting up; turn it off if yours does not, and no extra line is typed into the tabs.

## [1.1.1] - 2026-09-23
- A tab colour no longer moves to the neighbouring tab when the terminal is on a monitor scaled differently from the one at sign-in. The app was not per-monitor DPI aware, so Windows handed it tab positions scaled to the sign-in monitor while the rendered window stayed in real pixels: every tab was sampled a little left of where it is, a red tab's colour was recorded on the tab to its right, and others read as black. The app is now per-monitor aware, and a window whose tab positions still cannot be trusted to match its pixels is not sampled at all.

## [1.1.0] - 2026-09-17
- A restored tab colour can be cleared again. Windows Terminal keeps two colours per tab — the one the command line sets and the one the right-click picker sets on top of it — and the picker's Reset clears only the second, so a colour restored with --tabColor came straight back every time it was cleared. Colours are now applied the way the picker applies them, through the setTabColor action, which means lending Windows Terminal that action on an F13-and-up key for the second or two it takes to press it. The entries are removed by id afterwards, and by the next start if the app is killed mid-restore.
- Tabs no longer pick up colours they never had. The sampler was reading its own output: a restored colour is sampled again on the next snapshot, and un-blending multiplies a rounding error by more than three, so colours drifted until they saturated — #DD153D became #FF0051, another tab sank to black, and a plain tab acquired a grey. A colour now has to be read the same way twice before it is recorded, a reading close to the colour on file leaves it alone, and a colour read as cleared twice is forgotten — so clearing one in the terminal sticks.
- A frame the two background readings disagree about is no longer read at all. One bad frame that reported the tab strip as black had invented a colour for every uncoloured tab in the window at once.
- A tab scrolled out of an overflowing tab strip is skipped rather than sampled from the sliver of it still on screen.
- New setting, 'Put tab colours back when tabs are rebuilt', on by default. Turn it off to leave restored tabs uncoloured and Windows Terminal's settings untouched.
- A restore builds a window of its own again. Every restore aimed at one fixed window name, and wt.exe adds a tab to a window that already has that name rather than making one — so the second restore appended its tabs to the window the first had built, however many days earlier. The configured name is now the prefix and the time the restore ran completes it.

## [1.0.0] - 2026-09-16
- Initial release. Saves the open Windows Terminal tabs — including the live claude and codex conversations inside them — and rebuilds the window after a restart, each tab back in its own directory.
- Tabs are identified by WT_SESSION, the id Windows Terminal puts in every pane's environment. Process start times are never used: WSL's clock drifts from the host's across hibernate, by many hours and not by a constant offset.
- Session ids come from the running processes themselves — claude's open session handle inside WSL, the codex SQLite state — so two tabs in one folder still get their own conversation.
- Titles are matched to tabs by content, not by position: the agent's own session title, then the git branch (which is how a tab renamed 'ABC-1022' finds its way back to the checkout on ABC-1022-JWT-Alongside-Session-Auth), then the folder name. One tab and one title left over are paired; more than one are reported rather than guessed at.
- Restored tabs come back with the resume command waiting at the prompt, typed into the console input queue. Nothing runs until Enter — a dozen agents and their MCP servers all booting at once, right after a reboot, is not what you want. Turn on 'Run resume commands automatically' if you disagree.
- No shell-side hook to install: the WSL side is driven the same way as the Windows side, by attaching to the tab's console and typing into it.
