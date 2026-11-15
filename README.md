# MonitorLock

A tiny Windows tray tool that keeps the windows on one monitor exactly where you put them, even when another monitor changes resolution.

## Why

Play a game in exclusive fullscreen at a lower resolution (say CS2 at 1920×1080 on a 2560×1440 screen) and Windows rearranges the desktop. Any monitor to the right of the main one gets moved, and the windows on it get pushed around. GPU scaling doesn't help, because Windows still sees the resolution change.

MonitorLock remembers each window's position **relative to the monitor it's on**. When the layout changes, it puts the windows back on that monitor within a fraction of a second, without taking focus, so the game stays fullscreen.

## Use

- Run `MonitorLock.exe`. A monitor icon with a padlock appears in the tray: green when locked, grey when not.
- **Left-click** the icon to open the panel. Pick a screen on the mini map, then **Lock**. **Restore** puts the windows back right away, and **Start with Windows** is a checkbox.
- **Right-click** for the quick menu (per-monitor lock, restore, unlock, exit).
- On start, it locks the first non-main monitor automatically after 15 seconds.
- Windows you move on the locked monitor are remembered at their new spot, and new windows opened there are added automatically.
- Each restore is logged to `MonitorLock.log` next to the exe.

## Build

No SDK needed. Windows ships the C# compiler with .NET Framework 4.x:

```bat
build.bat
```

This produces `MonitorLock.exe`. It's a single file with no dependencies, and the icons are drawn in code.

## Limits

- It can't move windows that belong to programs running as administrator (Windows blocks that).
- Locks are kept in memory. After a restart it locks again automatically.
