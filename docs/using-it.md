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
| **The list** | Every game it found, each with a switch: on means HDR turns on for it. Switching one off keeps it listed, so a slip is one click to undo. The two buttons at the top right **look for games** again and **add a program** it missed, from a list of the programs that are running. |
| **A game's view** | The monitor symbol next to a game opens its **competitive resolution** ([Competitive resolution](competitive-resolution.md)), its **stretch** and its **SDR brightness**: Windows' *SDR content brightness* slider (0–100), set while that game runs with HDR on and put back when it closes. It decides how bright the desktop, SDR games and a game's SDR menus look in HDR. Move the slider yourself while the game runs and your level stays: it isn't changed back during the game or after it. The levels are kept in `brightness.txt`. The back arrow at the top left returns to the list. |
| **Bottom bar** | How many games have HDR, and the pencil, which opens the file behind what the list shows: `games.txt` with the games (and the programs to add), `resolutions.txt` in a game's resolution view, `brightness.txt` in its SDR brightness levels. |

**Settings**

| | |
|---|---|
| **Switching** | **Switch back after**: how long it waits after the last game closes before SDR (and the saved resolution) come back, 3 s, 10 s, 30 s or 60 s. A longer wait helps with launchers that close the game and start it again. **SDR while recording**, the same switch as on Live. **Pause shortcut**: Ctrl + Alt + H (it says so if another program has taken it). |
| **App** | **Start with Windows** and **Island** (see below), each with a switch that says **On** or **Off**, and **Updates** with the version you have (and the **Update** button when there is a new one). |
| **Bottom bar** | Where the files are (`%AppData%\HdrSwitch`), **Open folder** to see them in Explorer, and **Exit**, which quits. |

The flyout looks like Windows 11's own flyouts: solid, light or dark as Windows is set (**Settings › Personalisation › Colours › Choose your mode**, the Windows mode), with your **accent colour** for switches that are on, the chosen wait and the update button. Change the mode or the accent colour and an open flyout follows at once. With a **contrast theme** (high contrast) on, it uses that theme's colours, with thicker outlines. On Windows 11 its corners are rounded and Windows draws its border and shadow; on Windows 10 it has square corners, a shadow and a thin border.

The island is still glass: you see the desktop behind it, blurred. With **Settings › Personalisation › Colours › Transparency effects** off, it is solid dark grey.

Click anywhere else to close it. It stays on the monitor it opened on, and shows fewer than five rows of the games list (which then scrolls) when the whole panel wouldn't fit above the taskbar at a high display scale; Live and Settings scroll the same way when they don't fit. It works from the keyboard too: **Tab** or the arrow keys move between controls (a ring shows where you are), **Enter** or **Space** presses one, **Ctrl+Tab** and **Ctrl+Shift+Tab** go to the next or previous tab from anywhere (as do **Left** and **Right** on a tab), **Backspace** or **Esc** goes back from a game's resolution or brightness view or the programs to add – to the row you came from, scrolled where you left it – and **Esc** anywhere else closes it. Each tab keeps its place while the flyout is open. Once the mouse has moved the ring is hidden, and the first Enter or Space only shows it again; holding a key down presses once. Screen readers such as Narrator and NVDA read each control's name and state, the tabs as page tabs with the chosen one selected, and hear a switch flip, a label change and each step of an update. The icon is amber while HDR is on and grey in SDR. Amber always means HDR: in the flyout it marks only HDR, and the accent colour marks everything else that is on.

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

Turn off **Start with Windows**, choose **Exit**, then delete the folder with `HdrSwitch.exe` and `%AppData%\HdrSwitch`. That folder holds your lists (`games.txt`, `resolutions.txt`, `brightness.txt`, `recorders.txt`), the app's crash-recovery notes (`*.state`) and `error.log`, if anything ever went wrong.

