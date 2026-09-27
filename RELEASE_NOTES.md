# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- OLED Saver installs itself: open the exe from the extracted zip and accept the
  offer, and it moves into Program Files and starts as administrator at every
  logon, so the hotkey also works while an elevated program or game has focus.
- Fixed: other always-on-top windows, such as overlays that keep re-asserting
  themselves, could draw over the blackout. The blackout now stays on top of them.
- The release zip holds WPF's native libraries beside `OLEDSaver.exe` instead of
  extracting them to `%TEMP%` at first launch. Keep all the files together.
- The log splits each blackout's timing into its steps, to pin down a slow one.

- Fixed: a ticked monitor could end up blanking a different screen. The selection
  was stored as the Windows display slot (`\\.\DISPLAY2`), and Windows hands those
  slots back out after a monitor sleeps, is switched off at the panel, or the
  driver restarts. Monitors are now remembered by their own device path, which
  stays with the physical panel. Existing selections are converted on first run.
- The display list now shows each monitor's model name and Windows' own display
  number, instead of "Display N" counted in whatever order the monitors were
  enumerated in.
- First public release: true-black blackout overlay for OLED displays, driven by a
  global hotkey that keeps working while a game holds raw input.
- Per-monitor targeting, so an OLED can be blanked while an LCD stays on.
- Optional idle blackout that holds back while a full-screen app is in front.
- Start with Windows, tray icon, and `--toggle` / `--blackout` command line
  switches for Stream Deck buttons and shortcuts.
