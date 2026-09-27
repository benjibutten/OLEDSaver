# OLED Saver

A true-black screen blanking overlay for OLED displays on Windows. Press a hotkey
and every pixel goes to `#000000`, which on an OLED panel means the pixels are
switched off: no light, no power, no wear. Press it again — or hit any key — and
you are back exactly where you were.

Built with .NET 10 and WPF, and modelled on the input and startup handling in
[StreamDecky](https://github.com/benjibutten/StreamDecky).

## What it does

- **True black.** An opaque, borderless, topmost window per monitor filled with
  `#FF000000`. No transparency, no rounded corners, no drop shadow, no cursor —
  nothing that would light a pixel. Verified with a screen capture: every sampled
  pixel reads 0, including over the taskbar.
- **A global hotkey with modifiers.** `Ctrl + Alt + B` out of the box, and any
  combination of Ctrl / Alt / Shift / Win plus a key can be recorded in its place.
  The same combination turns the blackout back off.
- **Works while a game has focus.** Some games register raw input with
  `RIDEV_NOHOTKEYS`, which suppresses `WM_HOTKEY` delivery. A raw-input keyboard
  sink runs alongside `RegisterHotKey` as a fallback, and the two deduplicate so a
  press never counts twice.
- **Per-monitor targeting.** All displays, the primary only, or a ticked set —
  which is the point on a mixed desktop: blank the OLED and leave the LCD alone.
  A ticked monitor is remembered by its own device path and listed by its model
  name, so the selection stays on that panel when Windows renumbers the displays
  after a sleep, a power cycle or a driver restart. Hot-plugging or rearranging a
  monitor while the blackout is up re-covers everything.
- **Comes back on your terms.** A key press and a mouse click by default, mouse
  movement optionally, Escape and the hotkey always.
- **Idle blackout.** Optionally blanks the screen after N minutes with no input,
  and holds back while a full-screen app is in front so it never interrupts a film.
- **Start with Windows.** Straight into the tray at logon — as administrator when
  installed under Program Files, so the hotkey also works over elevated programs
  and games. See [Running as administrator](#running-as-administrator).
- **Scriptable.** `OLEDSaver.exe --toggle` blanks the screen or takes it back, and
  hands the request to the instance already running — so a Stream Deck button, a
  shortcut or another launcher can drive it. `--blackout` is the switch-on-only
  variant for when you never want it turning the screen back on.

## Getting started

Download the zip from the latest release, extract it, and open `OLEDSaver.exe`. It
offers to install itself: say yes, approve the UAC prompt, and it moves into
`C:\Program Files\OLEDSaver`, starts from there as administrator and turns on
**Start with Windows**. Say no to run it from where it is instead. Either way, the
DLLs in the zip belong beside the exe.

To build from source:

```powershell
dotnet build OLEDSaver.slnx
dotnet run --project src/OLEDSaver
```

The window is the settings; closing it leaves the app in the tray. Right-click the
tray icon to blank the screen, open settings, or quit.

| Argument | Effect |
| --- | --- |
| `--minimized` | Start in the tray with no window. This is what the startup entry uses. |
| `--blackout` | Blank the screen immediately, or tell the running instance to. Only ever switches on. |
| `--toggle` | Blank the screen, or take an existing blackout back down. This is the one a single Stream Deck button wants. |

Settings live in `%LocalAppData%\OLEDSaver\settings.json` and the log in
`%LocalAppData%\OLEDSaver\logs\oledsaver.log`. Both are reachable from the buttons
at the bottom of the settings window.

## Things worth knowing

**Mouse movement is not a dismissal trigger by default.** Plenty of machines
report mouse movement with nobody touching the mouse — a controller mapped to the
pointer, a game still holding it, an optical sensor on a glossy desk. On those the
blackout would lift a second after every press. A key or a click is an
unambiguous "I'm back"; drifting is not. Turn it on in **Coming back** if your
mouse is quiet, and raise the distance threshold if it is only nearly quiet.

**The idle blackout uses Windows' own idle timer** (`GetLastInputInfo`), so
anything that reports input keeps it at zero — the same devices as above. On a PC
like that the feature never triggers and the hotkey is the way to do it. Every
blackout and every dismissal is written to the log with its reason, which is the
quickest way to find out what is waking your screen.

**Displays are allowed to sleep.** A black OLED already draws next to nothing, so
Windows' normal display power-down is left alone. If your monitors reshuffle your
windows when they wake, switch on **Keep the displays awake while blacked out**
under **Power**.

**Notifications still light up.** A toast that arrives while the screen is black
will draw itself on top. Windows' focus assist / do-not-disturb is the tool for
that, and this app deliberately does not touch it.

## Running as administrator

Windows keeps a program without administrator rights from seeing input meant for
one that has them. While a game or tool running as administrator has focus, a
non-elevated OLED Saver may not see the hotkey.

Accepting the install offer on first launch sets this up: it starts as
administrator at every logon, without a UAC prompt. To do it by hand, extract the
release into `C:\Program Files\OLEDSaver` and run `OLEDSaver.exe` as
administrator once, with **Start with Windows** ticked.

That registers a Task Scheduler task, *OLEDSaver (your user name)*, and removes
the ordinary startup entry. The settings window says which of the two is in use.

The task is only created for an exe that nothing without administrator rights can
change — not the file, not its folder, and not by moving a folder above it.
Anywhere else, a program running as you could swap the exe and be started as
administrator at the next logon. `C:\Program Files` qualifies; a folder made
directly under `C:\`, such as `C:\Tools`, does not, since Windows lets every user
modify those. From there the app keeps using the ordinary startup entry.

`--toggle` and `--blackout` keep working from non-elevated launchers such as a
Stream Deck. To stop starting as administrator, untick **Start with Windows** in a
copy running as administrator, or delete the task in Task Scheduler.

## Layout

```
src/OLEDSaver
  App.xaml(.cs)             single instance, CLI arguments, shared styles, the vector icon
  MainWindow.xaml(.cs)      settings window; hosts the hotkey, the input sinks and the tray icon
  Helpers/                  user32 interop: hotkeys, window placement, idle time, raw input
  Input/                    hotkey definition, raw-input hotkey matcher, dismissal rules
  Models/AppSettings.cs     everything that is persisted
  Services/                 blackout controller, settings store, displays, startup registry, idle watcher
  Views/BlackoutWindow      the true-black surface
tests/OLEDSaver.Tests       the pure logic: hotkeys, matching, dismissal, idle rule, settings, displays
```

Anything that can be decided without user32 is a pure class with tests around it:
`HotkeyDefinition`, `RawInputHotkeyMatcher`, `BlackoutDismissEvaluator`,
`IdleBlackoutEvaluator`, `DisplayService.ResolveTargets` and `AppSettings`
normalization. The interop sits behind them in one place.

```powershell
dotnet test OLEDSaver.slnx
```
