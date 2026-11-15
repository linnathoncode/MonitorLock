# MonitorLock

A tiny Windows tray tool that keeps the windows on one monitor exactly where you put them, even when another monitor changes resolution.

## Why

Play a game in exclusive fullscreen at a lower resolution (say CS2 at 1920×1080 on a 2560×1440 screen) and Windows rearranges the desktop. Any monitor to the right of the main one gets moved, and the windows on it get pushed around. GPU scaling doesn't help, because Windows still sees the resolution change.

MonitorLock remembers each window's position **relative to the monitor it's on**. When the layout changes, it puts the windows back on that monitor within a fraction of a second, without taking focus, so the game stays fullscreen.

## Use

- Run `MonitorLock.exe`. A monitor icon with a padlock appears in the tray: green when anything is locked, grey when not.
- **Left-click** the icon to open the panel. It shows your screens in their real layout, each with its own **padlock button**. Click a padlock to lock or unlock that screen. Any combination can be locked at once.
- Everything starts **unlocked**.
- **Right-click** for the quick menu (lock each screen, restore now, exit).
- While a screen is locked, windows you move on it are remembered at their new spot, new windows opened there are added, and windows you drag to another screen are let go.
- Each restore is logged to `MonitorLock.log` next to the exe.

## Build

No SDK needed. Windows ships the C# compiler with .NET Framework 4.x:

```bat
build.bat
```

This produces `MonitorLock.exe`. It's a single file with no dependencies, and the icons are drawn in code.

## Limits

- It can't move windows that belong to programs running as administrator (Windows blocks that).
- Locks are kept in memory, so everything starts unlocked after a restart.

