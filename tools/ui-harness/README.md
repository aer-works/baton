# UI driving harness

Tooling for verifying UI behaviour against the **running** product — the desktop app driven by
window handle.

It exists because a UX evaluation produced confidently-wrong findings that were only corrected by
actually driving the UI: the reviewed output *was* rendered (under a non-default tab), and Author
*does* draw a live DAG (inside a collapsed expander). Reading source and clicking around was not
enough for either.

Not wired into `pixi run` or CI: these drive a real desktop session, so they are a deliberate
manual tool. Issue #313 builds automated journeys on top of them.

## Desktop (Windows, Avalonia)

| Script | Purpose |
|---|---|
| `Bring-Foreground.ps1 -Handle 0x… [-Topmost] [-Release]` | Legally front the target so clicks land; `-Topmost` pins it for a drive, `-Release` clears it |
| `List-Windows.ps1 -ProcessId <pid>` | Every visible top-level window for a process |
| `Capture-Window.ps1 -TitleLike <s> -OutPath <png>` | Capture the main window by title match |
| `Capture-Handle.ps1 -Handle 0x… -OutPath <png> [-ClickX -ClickY]` | Capture any HWND, optionally clicking first |
| `Click-Capture.ps1 -X -Y -OutPath <png>` | Click a window-relative point, then capture |
| `Click-Type.ps1 -Handle 0x… -X -Y -Text <s>` | Click a field and type into it |
| `Scroll-Capture.ps1 -X -Y -Notches <n> -OutPath <png>` | Scroll (negative = down), then capture |

Four things that are not obvious and cost real time to discover:

- **A click goes to whatever window is on top at that pixel — not to the `-Handle` you passed.**
  These are real screen events (`SetCursorPos` + `mouse_event`). The scripts call
  `SetForegroundWindow` first, but Windows *refuses* that call from a process that is not already
  foreground: it fails silently and returns. So if anything covers the target, **the click lands in
  that other application**, while `PrintWindow` still renders the target correctly — the screenshot
  looks exactly like a UI that ignored you. During the M25 evaluation this manufactured three false
  product defects in a row (including a `Start new chat` button that was fine), and later sent a
  run of clicks into an unrelated Chrome window.

  Since #356 the scripts check `WindowFromPoint` against the target's root window and **refuse with
  exit code 2** rather than clicking a stranger. If you see `REFUSED`, front the window with
  `Bring-Foreground.ps1 -Handle 0x… -Topmost` and retry — do not work around it. A background
  session cannot front the app with `SetForegroundWindow` alone (that is what is refused);
  `Bring-Foreground.ps1` uses `AttachThreadInput` to acquire the right legally, and verifies it
  worked. `-Topmost` keeps the window up for a multi-step drive; call `-Release` when done. Earlier
  advice here said to "click twice"; that was a description of the symptom and it is **wrong**: if
  another window is on top, the second click misses too.
- **`Click-Type.ps1` is the most dangerous, and used to lie.** `SendKeys` targets whatever window
  actually holds focus, so a misdirected click meant text was typed somewhere else entirely while
  the script printed `OK typed N chars`. It now refuses on the same check, but the underlying
  hazard remains: **never trust its return value** — screenshot the field afterwards. For anything
  load-bearing, drive the daemon's HTTP API instead and keep the UI for what you can *see*.
- **`List-Windows.ps1` reports a *logical* rect; captures and clicks are *physical* pixels.** On a
  150% display it prints `1215x808` for a window whose capture is `1804x1203`. Coordinates read off
  a capture are the correct ones to pass; coordinates taken from `List-Windows.ps1` land ~1.5x off.
- **Avalonia modal dialogs and popups are separate HWNDs** and never appear in
  `Process.MainWindowTitle` — this includes `ComboBox` dropdowns, not just dialogs. One can be open
  and fully invisible to `Get-Process`. Use `List-Windows.ps1`, then `Capture-Handle.ps1`.
- Capture uses `PrintWindow` with `PW_RENDERFULLCONTENT` (`0x2`), which asks the compositor to
  re-render the window into a bitmap rather than copying screen pixels — so it works when the window
  is occluded, on a second monitor, or off-screen. Geometry comes from
  `DwmGetWindowAttribute(hwnd, 9, …)` (extended frame bounds), not `GetWindowRect`.
- The scripts save and restore the cursor position, so driving the app does not steal the pointer
  from whoever is using the machine.

Alt-mnemonics only respond after clicking into the view *and* navigating away from Home.

## Coverage inventory

```
python tools/ui-harness/inventory.py
```

Parses `src/**/*.axaml` for interactive controls, and reports **disclosure containers**
separately — expanders, tabs and popup menus, the places capability hides. Every finding the
evaluation initially got wrong was behind one of those.

The point is that coverage becomes a checklist rather than a memory of where someone clicked. The
current walked/unwalked state is tracked on #313.
