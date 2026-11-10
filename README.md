<p align="center"><img src="assets/logo.png" width="120" alt="Folder Mirror logo"></p>

<h1 align="center">Folder Mirror</h1>
<p align="center"><b>Your files. Always in sync.</b><br>
A modern, safe folder synchronization utility for Windows — mirror, one-way, two-way and incremental backup, with a full preview before anything changes.</p>

<p align="center"><a href="https://github.com/unupunct/Folder-Mirror/releases/latest"><b>⬇ Download the installer or the portable exe</b></a></p>

![Dashboard](docs/screenshots/dashboard.png)

## Highlights

- **Preview first.** Every run starts from the real folder state. A colour-coded table shows exactly what is *new*, *modified*, *deleted*, *unchanged* or in *conflict*, with filters and a summary of data and free space. Nothing is touched during preview.
- **Four modes.** Mirror · One-way · Two-way (tracks previous state, never overwrites conflicts silently) · Incremental backup (keeps every old version).
- **Safe by default.** Deleted files go to a dated archive, overwritten files can be versioned, same/nested folders and dangerous destinations are refused, disconnected drives are detected, unattended runs never delete unless you allow it.
- **Robust engine.** Streaming copies via temp file → flush → optional SHA-256 verify → atomic rename. Cancellation, live progress, speed and ETA, locked-file retries, long paths, parallel transfers, interrupted-run recovery.
- **Schedules that work in the background.** Real Windows Task Scheduler tasks that start the same `FolderMirror.exe` in headless mode (every 15/30 min, hourly, daily, weekly, custom, at startup/sign-in, only when drives are available).
- **History you can trust.** SQLite history with search, filters, per-run reports and CSV export. Tray icon with notifications. Dark/light themes, accent colours, 5 UI languages (navigation and main buttons).
- **Self-contained.** One `FolderMirror.exe`, no .NET install, fully offline, no network access, no stored passwords.

## Screenshots

| Preview a mirror run | Two-way conflicts |
|---|---|
| ![Mirror preview](docs/screenshots/preview-mirror.png) | ![Conflicts](docs/screenshots/preview-conflicts.png) |

| Profile wizard | History & reports |
|---|---|
| ![Wizard](docs/screenshots/wizard-2-mode.png) | ![History](docs/screenshots/history.png) |

| Scheduled tasks | Settings |
|---|---|
| ![Schedules](docs/screenshots/schedules.png) | ![Settings](docs/screenshots/settings.png) |

<details><summary>More: profiles, wizard steps, schedule dialog, light theme, about</summary>

![Profiles](docs/screenshots/profiles.png)
![Wizard step 1](docs/screenshots/wizard-1-folders.png)
![Wizard step 3](docs/screenshots/wizard-3-options.png)
![Wizard step 4](docs/screenshots/wizard-4-review.png)
![Schedule dialog](docs/screenshots/schedule-dialog.png)
![Light theme](docs/screenshots/dashboard-light.png)
![About](docs/screenshots/about.png)
</details>

## Install

Download from the [latest release](https://github.com/unupunct/Folder-Mirror/releases/latest):

- **`FolderMirror_Setup.exe`** – installer (Start Menu + optional desktop shortcut, upgrade-safe, clean uninstall; asks before removing your data).
- **`FolderMirror.exe`** – portable single file; copy it anywhere and run.

Windows 10/11 x64. No administrator rights needed.

## How the modes behave

| Mode | Copies new/changed | Deletes | Notes |
|---|---|---|---|
| Mirror | yes | destination-only files (archived by default) | preview + explicit confirmation required |
| One-way | yes | never | |
| Two-way | both directions | propagated deletions (archived) | conflicts are listed; you pick *use source / use destination / keep both / leave untouched* |
| Incremental | yes | never | previous versions kept in `_FolderMirror\Versions\<date>` |

`_FolderMirror` inside a destination holds archives, versions and a marker file; it is never synchronized.

## Build from source

Requires the .NET 10 SDK and (for the installer) Inno Setup 6.

```powershell
dotnet test tests/FolderMirror.Tests          # engine tests (temporary folders only)
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1   # tests + single-file exe + installer -> release/
```

Layout: `src/FolderMirror.Core` (engine, SQLite, scheduling – UI-free), `src/FolderMirror` (WPF + MVVM, CommunityToolkit.Mvvm, Serilog), `tests/FolderMirror.Tests` (xUnit), `installer/` (Inno Setup).
`FolderMirror.exe --screenshots <dir> --demo-root <dir>` renders the README screenshots from throw-away demo data (set `FOLDERMIRROR_DATA` to keep it away from real data).

## Known limitations

Language selection covers navigation, dashboard and main buttons only · no Volume Shadow Copy for locked files · NTFS ACLs, alternate streams and symlinks/junctions are not copied (links are skipped with a warning) · folder timestamps are not preserved.

## License

MIT © 2026 Folder Mirror
