FOLDER MIRROR 1.0.0
Your files. Always in sync.
=====================================================

Folder Mirror keeps folders in sync between local drives, USB drives and
network shares. It works fully offline and needs no .NET runtime: everything
is inside FolderMirror.exe (self-contained, Windows 10/11 x64).

INSTALL
  Run FolderMirror_Setup.exe (no administrator rights needed; you may choose
  "install for all users"). Or just copy FolderMirror.exe anywhere and run it.
  Updating: run a newer setup over the old one. Profiles and history are kept
  (they live in %LOCALAPPDATA%\FolderMirror).
  Uninstall: Settings > Apps. You are asked whether to remove your data.

FIRST STEPS
  1. Dashboard > New Synchronization. The wizard asks for source and
     destination, the mode, options, and a final review.
  2. Synchronization > Preview. Folder Mirror reads the real folders and lists
     every file it would copy, update or delete. Nothing changes during preview.
  3. Start Synchronization.

MODES
  Mirror       Destination becomes an exact copy. Extra destination files are
               removed (moved to _FolderMirror\Archive\<date> by default).
               Needs a preview and an explicit deletion confirmation.
  One-way      Copies new and changed files. Never deletes.
  Two-way      Carries changes (and deletions) both ways using a local state
               database. Files changed on both sides are conflicts; they are
               never overwritten silently. You choose per file: use source,
               use destination, keep both, or leave untouched.
  Incremental  Copies new and changed files and keeps every older version in
               _FolderMirror\Versions\<date>. Never deletes.

SAFETY
  - Source = destination, nested folders, drive roots (Mirror), Windows and
    Program Files destinations are refused.
  - Unavailable or disconnected drives are detected before and during a run.
  - Files are copied to a temporary file, flushed, optionally verified with
    SHA-256, then renamed into place. Interrupted runs leave no partial files.
  - Scheduled (unattended) runs only delete when you allow it for that
    schedule, and stop if the deletion count looks suspicious.
  - The folder "_FolderMirror" inside a destination holds archives, versions
    and a marker file. It is never synchronized or mirrored away.

SCHEDULING
  Scheduled Tasks creates real Windows Task Scheduler tasks that run
  "FolderMirror.exe --run-profile <id> --background" (same executable, no
  window). Options: every 15/30 min, hourly, daily, weekly, custom, at Windows
  start, at sign-in, only when both locations are available, pause all.

DATA, LOGS AND PRIVACY
  Data:  %LOCALAPPDATA%\FolderMirror\foldermirror.db (SQLite)
  Logs:  %LOCALAPPDATA%\FolderMirror\logs (configurable)
  Folder Mirror makes no network connections and stores no passwords. Network
  shares are accessed with your current Windows sign-in.

KNOWN LIMITATIONS
  - Language selection translates navigation, dashboard and main buttons only.
  - Locked files are retried briefly and then reported; there is no Volume
    Shadow Copy support.
  - NTFS alternate data streams, ACLs/owners and symbolic links/junctions are
    not copied (links are skipped with a warning).
  - Folder timestamps are not preserved. Two-way sync always preserves file
    timestamps.
  - Case-only renames are treated as the same file on Windows.

License: MIT. https://github.com/unupunct/Folder-Mirror
