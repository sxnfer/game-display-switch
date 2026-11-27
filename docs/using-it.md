# Using it

[← Back to the README](../README.md)

## Download and use

1. Download **HdrSwitch.exe** from the [latest release](https://github.com/sxnfer/game-display-switch/releases/latest).
2. Put it in a folder that will stay put, for example `C:\Tools\Display Switch`.
3. Double-click it. Windows will probably say **"Windows protected your PC"**, because the app isn't code-signed. Click **More info › Run anyway**. Every release is built by GitHub from the code in this repository, and comes with its SHA256 checksum (`SHA256SUMS.txt`).
4. An **H** icon appears in the tray (click the **^** near the clock if you don't see it). Click it, open **Settings** and turn on **Start with Windows**.

That's it. It finds your games on its own (Steam, Epic, GOG, Ubisoft Connect, the EA app, Battle.net, Riot, Rockstar, the Xbox app, and game folders such as `Games` on every drive). Launchers and tools that install next to games and run all day, such as Riot Client, Rockstar's launcher and Social Club, Wallpaper Engine and Steam's runtimes, are left out, so they don't keep HDR on. GOG's DOS and adventure classics all run as the same program (DOSBox or ScummVM), so they aren't added on their own: start one and use **add a program** if you want HDR for them.

Needs Windows 10 or 11 and a monitor with HDR available in **Settings › System › Display**. Nothing else to install.

With more than one HDR screen, HDR goes on the screen the game is on. It starts on the screen the game used last time (the main one the first time), and once the game's window is up, a game on another HDR screen gets HDR there while the first goes back to SDR. Other screens are left alone.

## The flyout

Click the icon (left or right button) and a small panel slides in by the taskbar. It has three tabs, so each thing has one place: **Live** shows what is switched right now, **Games** holds your games and what each one changes, **Settings** holds everything that applies to all of them. It opens on **Live** while a game runs, while the wait before switching back counts down, while it is paused or while a recorder holds HDR off; otherwise on the tab you used last.

**Live**

| | |
|---|---|
| **Status** | The running game's name, with the monitor it plays on and when things go back: `back to SDR 3 s after it closes`, then `back to SDR in 2s` counting down once it has closed (or the desktop's resolution, for a game with only a competitive resolution). Without a game: `Waiting for a game`, `Paused` or `No HDR display`. A setup hint (for example about a missing resolution) stays under it until the game closes. |
| **Layers** | One card with what a game can change, in the order it applies: **HDR**, **SDR brightness**, **Competitive area**, **Stretch** and **NVIDIA scaling**. A layer in effect has a lit icon and its value on the right: an amber **ON** for HDR that Display Switch turned on, `40 of 100` for the brightness, `2368 × 1332` for the area and the stretch, `No scaling` when it set NVIDIA's scaling for the area (put back afterwards). One that isn't is dimmed and says `Off`, `Windows'` or `Unchanged` while a game runs, and `—` when there is no game. The line under each says why, for example `Held off: OBS Studio is running` or `Not set for this game`. Screen readers read each layer as text, its name and value. |
| **Holds** | **SDR while recording** with its switch ([Recording and streaming](recording.md)); while a recorder runs it says which, with a red dot: `OBS Studio is running · HDR held off`. While paused, a yellow bar says `Paused · nothing switches`, with **Resume**. |
| **Pause** | The button at the top right. Stops all switching: what Display Switch turned on goes back at once, and nothing switches until you resume. **Ctrl+Alt+H** does the same from anywhere, even in a game. A pause lasts until you resume or restart the app. |
| **Update** | When there is a new version, a bar at the bottom says `Version … is ready` with an **Update** button; while it updates the button shows each step: Downloading, Verifying, Restarting – or *After the game* while a game holds it back, with the reason beside it. |

**Games**

| | |
|---|---|
| **The library** | Every game it found, by name. **Search** at the top: type and the list keeps the games whose name or exe name has every word you typed (`counter 2`, `cs2.exe`); typing anywhere on the list types into it, **Backspace** takes a letter off, **Esc** clears it and **Enter** opens the first game it found. Under each name, what the game changes: `HDR · SDR 40 · 2368 × 1332`, `HDR · 2368 × 1332 · stretched 4:3`, or `Stays in SDR` for a game set to never switch (its SDR brightness only counts with HDR, so it isn't listed there). A game that is running says `· running` in amber. The switch is HDR: on, or never. Switching one off keeps it listed, so a slip is one click to undo. Click a game anywhere but its switch (or its arrow) to open its page. The two buttons at the top right **look for games** again and **add a program** it missed, from a list of the programs that are running. |
| **A game's page** | The game's name, its exe and launcher, and its layers in the same order as on Live, each with its own control. **HDR**: on, or never. **SDR brightness**: Windows' *SDR content brightness* (0–100), set while that game runs with HDR on and put back when it closes. It decides how bright the desktop, SDR games and a game's SDR menus look in HDR. Click it to open a slider (it shows the nits each level means) and **Use Windows' level**; a new level is saved when you let go of the slider (from the keyboard, once the keys rest) and applied at once if the game is running. Move Windows' own slider yourself while the game runs and your level stays: it isn't changed back during the game or after it. **Competitive area** ([Competitive resolution](competitive-resolution.md)): a switch; once on, a picture of your screen to scale with the area drawn 1:1 in the middle and its size and diagonal, the size chosen and **More…** for every size. **Stretch**: the game's own, lower resolution stretched over the area – it needs an area first, and says so. **NVIDIA scaling**: what Display Switch does by itself – *Set to No scaling while it runs* for an area without a stretch, *Not needed* otherwise, *Not available* without NVIDIA graphics. When a size has to be created in NVIDIA Control Panel first, or the latest notification is about this game, a yellow bar at the top of the page says so. **Games** at the top left returns to the library, to the game you came from. |
| **Bottom bar** | In the library, how many games have HDR and how many there are (`8 with HDR · 10 games`; while searching, `3 of 10 games`). The pencil opens the file behind what is showing: `games.txt` in the library (and the programs to add), `resolutions.txt` on a game's page and in its lists of sizes, `brightness.txt` on its page while the SDR brightness slider is open. |

**Settings**

| | |
|---|---|
| **Switching** | **Switch back after**: how long it waits after the last game closes before SDR (and the saved resolution) come back, 3 s, 10 s, 30 s or 60 s. A longer wait helps with launchers that close the game and start it again. **SDR while recording**, the same switch as on Live. **Pause shortcut**: Ctrl + Alt + H (it says so if another program has taken it). |
| **App** | **Start with Windows** and **Island** (see below), each with a switch that says **On** or **Off**, and **Updates** with the version you have (and the **Update** button when there is a new one). **Switch log**: what Display Switch did, line by line; **Open** shows it in Notepad (or whatever opens `.log` files on your PC), and the row says so if nothing has been written yet. |
| **Bottom bar** | Where the files are (`%AppData%\HdrSwitch`), **Open folder** to see them in Explorer, and **Exit**, which quits. |

The flyout looks like Windows 11's own flyouts: solid, light or dark as Windows is set (**Settings › Personalisation › Colours › Choose your mode**, the Windows mode), with your **accent colour** for switches that are on, the chosen wait and the update button. Change the mode or the accent colour and an open flyout follows at once. With a **contrast theme** (high contrast) on, it uses that theme's colours, with thicker outlines. On Windows 11 its corners are rounded and Windows draws its border and shadow; on Windows 10 it has square corners, a shadow and a thin border.

The island looks like the flyout: solid, in the Windows mode, with a thin outline and a soft shadow. Its dot says what happened: **amber** for HDR (and `HDR on` is tinted amber too), your **accent colour** for a competitive resolution, an update or other news, grey for SDR, the caution colour (yellow, or dark amber in light mode) for `Paused`, and **red** for a recording app or a failed update. With a contrast theme on it uses that theme's colours with a thicker outline, and the words say whether HDR is on. Change the mode, the accent colour or the contrast theme and an island on screen follows at once.

Click anywhere else to close it. It stays on the monitor it opened on, and shows fewer than five rows of the games list (which then scrolls) when the whole panel wouldn't fit above the taskbar at a high display scale; Live and Settings scroll the same way when they don't fit. It works from the keyboard too: **Tab** or the arrow keys move between controls (a ring shows where you are), **Enter** or **Space** presses one, **Ctrl+Tab** and **Ctrl+Shift+Tab** go to the next or previous tab from anywhere (as do **Left** and **Right** on a tab), **Backspace** or **Esc** goes back from a game's page, a list opened from it or the programs to add – to the row or card you came from, scrolled where you left it – and **Esc** anywhere else closes it (in the library it clears the search first). In the library **Up** and **Down** go from game to game, **Enter** or **Right** opens a game's page and **Space** flips its HDR switch (**Tab** also stops on each switch). On the SDR brightness slider **Left** and **Right** move it by 1, **Page Up** and **Page Down** by 10, **Home** and **End** to 0 and 100. Each tab keeps its place while the flyout is open. Once the mouse has moved the ring is hidden, and the first Enter or Space only shows it again; holding a key down presses once. Screen readers such as Narrator and NVDA read each control's name and state, the tabs as page tabs with the chosen one selected, each game with what it changes (its HDR switch is a check box of its own), the search box's text, the slider's level and whether the SDR brightness card is open, and hear a switch flip, a label change and each step of an update. The icon is amber while HDR is on and grey in SDR. Amber always means HDR: in the flyout it marks only HDR, and the accent colour marks everything else that is on.

**If a game starts at the wrong size.** Open **Settings › Switch log**. Every time Display Switch decides something about HDR, a resolution or NVIDIA's scaling, it writes one line to `%AppData%\HdrSwitch\display.log`: the time, what happened, the details as `name=value`, and after `->` what it did and why. It holds game and display names, modes and settings and nothing else, it stays on your PC, and it is kept under about 500 KB by dropping the oldest half. Find the time you started the game and read down from there:

- `scan-detected` or `steam-running`: when the game was noticed. `ageMs` is how long its process had already been running; a few thousand means it was seen late and may have picked its size before the switch. `steam-running … -> no match` means Steam's Play wasn't recognised as this game, so it was only caught once its process appeared.
- `apply`: the display chosen (`display`, `why`), the mode wanted (`want`, and `area` for a stretched one), the mode that was there (`current`, `saved`), and the outcome. `applied` is a switch. `unavailable` means the display doesn't offer that size at that refresh rate, and `sizeExistsAtHz` lists the rates at which it does (`none`: create it in NVIDIA Control Panel). `skipped` and `failed` give the reason in brackets: paused, another game's mode already holds the display, the desktop was already at that size, Windows refused.
- `scaling-take` and `scaling-restore`: NVIDIA's scaling before and after. `not taken` or `FAILED` explains a game in exclusive fullscreen that fills the whole panel.
- `picture`: what the display path looked like before and after the switch, as desktop size `>` signal size `/` scaling, and for a stretched resolution whether it really is stretched over the area.
- `display-changed`: Windows reported a display change (a game setting its own mode, HDR toggled, the monitor waking, Alt+Tab out of exclusive fullscreen), with the state found and what was done. `NOT in our mode while a profiled game runs` is the moment the resolution was lost under a running game.
- `power` and `session`: sleep, resume, lock and unlock, noted because a mode is often lost around them.
- `keep-mode`, `revert`, `settle`, `grace-start`, `hdr-on`, `hdr-off`, `hint`: the mode set again after an HDR switch, the display put back, the wait before that, HDR per display, and each notification's text.

If you report a problem, the lines from the game's start to its exit are what tells us why.

## The island

When something happens, a small pill drops in at the top-centre of the screen and leaves again after a few seconds:

| | |
|---|---|
| **A game starts** | The game's name with `HDR on`, and its competitive resolution if it has one. It then shrinks to a dot and `HDR on`. |
| **A game closes** | `SDR in 3s` (or the wait you chose), counting down, then `SDR`. Starting a game again in that time cancels it. |
| **Pause** | `Paused` when you pause with Ctrl+Alt+H or the flyout, and `HDR on` or `Resumed` when you resume. |
| **Recording** | `OBS Studio · SDR` with a red dot beside it, when **SDR while recording** takes HDR off. |
| **Updates and scans** | A new version, a finished update, the games a scan found. |

It never takes focus and has no taskbar or Alt+Tab entry. It doesn't show over a game in exclusive fullscreen, over any other fullscreen window, or in presentation mode: it waits up to 10 seconds for the screen to be free, and what still matters then arrives as an ordinary Windows notification. While a game runs, clicks go straight through it; otherwise resting the mouse on it keeps it there and a click opens the flyout. Longer setup hints (a resolution that has to be created in NVIDIA Control Panel, say) are always ordinary notifications, and stay at the top of the flyout's **Live** tab.

Text that changes while you watch rolls to its new value one character at a time: the countdown, the status card, a setting's **On** and **Off**, the number of games that are on. Only what changed moves, so `SDR in 3s` → `SDR in 2s` turns just the digit.

Turn it off with the **Island** switch in the flyout's **Settings**; everything it would have said then comes as a notification, as in earlier versions. Movement follows the Windows setting **Accessibility › Visual effects › Animation effects**: with that off, the island and the flyout appear and change without animation. `HdrSwitch.exe --island-demo` shows every state in turn without switching anything, also next to a running copy.

The island's shapes and motion are inspired by Apple's Dynamic Island and by the open-source React project islandcn (MIT); no code is taken from it.

## Updates

Display Switch checks this repository once a day. When there is a new version, an **Update** button appears in the flyout: in a bar at the bottom of **Live**, and in **Settings › Updates**. One click downloads it, checks it against the release's SHA256 checksum and that checksum file's signature, and restarts the app. A release without a valid signature is never offered: the signature, and the version it vouches for, are checked before the button appears, and again before installing. The update check is the only time the app goes online.

An update never restarts the app in the middle of a game, since that would switch HDR and the resolution back under it. Clicking **Update** while a game runs downloads and checks the update, and the button says *After the game*; it installs once the game has closed and the display is back. If the new version is in place but doesn't start, you're told to restart Display Switch to finish.

## Uninstall

Turn off **Start with Windows**, choose **Exit**, then delete the folder with `HdrSwitch.exe` and `%AppData%\HdrSwitch`. That folder holds your lists (`games.txt`, `resolutions.txt`, `brightness.txt`, `recorders.txt`), the app's crash-recovery notes (`*.state`), the switch log (`display.log`) and `error.log`, if anything ever went wrong.

