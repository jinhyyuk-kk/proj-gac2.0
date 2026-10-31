# GAC 2.0 — Easter Egg Instructions for Claude Code

> Place this file at the repository root next to `CLAUDE.md`. Implement it exactly as written. Do not ask questions about it.

## What it is

A hidden **About** item in the GAC Console. It is not visible by default. Once revealed, it appears as the **last item of the top-left menu bar** and opens a small dialog with exactly this text:

```
MIT Licence. Develop by JH
```

Keep the text **exactly** as above, character for character: `Licence` (not `License`), `Develop` (not `Developed`). Do not translate it to Korean, do not add a version number, logo, link, or any other text to the dialog body.

## Where

- **Console only** (`Gac.Console`, which also powers `GAC-Demo.exe`). Not in the Tray App, not in installers, not in the Agent.
- The top-left `MenuStrip` of the Console main window. The About item is always the **last** top-level menu item, after every other menu.

## How it is hidden

- On startup, the About menu item exists but `Visible = false`.
- It must not appear in tooltips, keyboard shortcuts lists, help screens, the README, or `docs/`.
- It must work for every role (Owner, Admin, Operator, Viewer) and also in `GAC-Demo.exe` (the demo uses the same Console code).

## How to reveal it

Click the **GAC logo / title at the top-left of the Console window 5 times within 3 seconds**.

- On the 5th click, set the About menu item to `Visible = true`. No sound, no popup, no message — it just appears.
- It stays visible until the Console is closed. Next launch, it is hidden again.
- Clicking fewer than 5 times, or slower than 3 seconds total, does nothing. Reset the counter when the 3-second window passes.
- The logo click must not trigger anything else (no navigation, no refresh) so normal users never notice.

## The dialog

- Small, centered on the Console window, not resizable, no minimize/maximize buttons.
- Window title: `About`
- Body: the single line `MIT Licence. Develop by JH`, centered.
- One button: `OK` (also closes on `Esc` / `Enter`).
- Uses the Console's normal font and colors; works at 100%–200% display scaling.

## Code rules

- Put the logic in one small class, e.g. `Gac.Console/EasterEgg/AboutEasterEgg.cs`, wired into the main form with a few lines. Keep the click counting testable without UI (pass in a clock).
- Add a unit test: 5 clicks within 3 s reveals; 4 clicks does not; 5 clicks spread over more than 3 s does not; the dialog text equals `"MIT Licence. Develop by JH"`.
- Name things plainly in code (`AboutEasterEgg`, `RevealClickCount = 5`, `RevealWindow = TimeSpan.FromSeconds(3)`). It is hidden from users, not from the codebase.
- No network access, no file writes, no telemetry.

## Related

- The repository root must contain a standard **MIT License** `LICENSE` file with copyright holder `JH` and year `2026`. (The file name and legal text use the standard spelling `LICENSE`; only the Easter-egg dialog uses `Licence`.)
