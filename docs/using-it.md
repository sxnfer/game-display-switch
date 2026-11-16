# Using it

[← Back to the README](../README.md)

## Download and use

1. Download **HdrSwitch.exe** from the [latest release](https://github.com/sxnfer/game-display-switch/releases/latest).
2. Put it in a folder that will stay put, for example `C:\Tools\Display Switch`.
3. Double-click it. Windows will probably say **"Windows protected your PC"**, because the app isn't code-signed. Click **More info › Run anyway**. Every release is built by GitHub from the code in this repository, and comes with its SHA256 checksum (`SHA256SUMS.txt`).
4. An **H** icon appears in the tray (click the **^** near the clock if you don't see it). Click it and turn on **Start with Windows**.

That's it. It finds your games on its own (Steam, Epic, GOG, Ubisoft Connect, the EA app, Battle.net, Riot, Rockstar, the Xbox app, and game folders such as `Games` on every drive).

Needs Windows 10 or 11 and a monitor with HDR available in **Settings › System › Display**. Nothing else to install.

With more than one HDR screen, HDR goes on the screen the game is on. It starts on the screen the game used last time (the main one the first time), and once the game's window is up, a game on another HDR screen gets HDR there while the first goes back to SDR. Other screens are left alone.

## The flyout

Click the icon (left or right button) and a small panel slides in by the taskbar:

| | |
|---|---|
| **Status** | `HDR on` with the game's name, `Waiting for a game`, or `No HDR display`, with an amber dot while HDR is on. A setup hint (for example about a missing resolution) stays here until the game closes. An **Update to …** button appears here when there is one; while it updates it shows each step: Downloading, Verifying, Restarting. |
| **Tiles** | **Start with Windows**, **SDR while recording** ([Recording and streaming](recording.md)) and **Island** (see below). A tile is white glass and says **On** while it is on, clear and **Off** otherwise. |
| **Switch back after** | How long it waits after the last game closes before SDR (and the saved resolution) come back: 3 s, 10 s, 30 s or 60 s, one click for the next. A longer wait helps with launchers that close the game and start it again. |
| **Pause** | Stops all switching: what Display Switch turned on goes back at once, and nothing switches until you click **Resume**. **Ctrl+Alt+H** does the same from anywhere, even in a game. A pause lasts until you resume or restart the app. |
| **Games** | Every game it found, each with a switch: amber means HDR turns on for it. Switching one off keeps it listed, so a slip is one click to undo. The two round buttons in the header **look for games** again and **add a program** it missed, from a list of the programs that are running. |
| **SDR brightness** | The monitor symbol next to a game also has **SDR brightness**: Windows' *SDR content brightness* slider (0–100), set while that game runs with HDR on and put back when it closes. It decides how bright the desktop, SDR games and a game's SDR menus look in HDR. The levels are kept in `brightness.txt`. |
| **Bottom bar** | The pencil opens `games.txt`, **Exit** quits. |

The flyout and the island are glass: you see the desktop behind them, blurred. With **Settings › Personalisation › Colours › Transparency effects** off, they are solid dark grey.

Click anywhere else, or press Esc, to close it. The icon is amber while HDR is on and grey in SDR. Amber always means HDR; the Windows accent colour isn't used.

## The island

When something happens, a small pill drops in at the top-centre of the screen and leaves again after a few seconds:

| | |
|---|---|
| **A game starts** | The game's name with `HDR on`, and its competitive resolution if it has one. It then shrinks to a dot and `HDR on`. |
| **A game closes** | `SDR in 3s` (or the wait you chose), counting down, then `SDR`. Starting a game again in that time cancels it. |
| **Pause** | `Paused` when you pause with Ctrl+Alt+H or the flyout, and `HDR on` or `Resumed` when you resume. |
| **Recording** | `OBS Studio · SDR` with a red dot beside it, when **SDR while recording** takes HDR off. |
| **Updates and scans** | A new version, a finished update, the games a scan found. |

It never takes focus and has no taskbar or Alt+Tab entry. It doesn't show over a game in exclusive fullscreen, over any other fullscreen window, or in presentation mode: it waits up to 10 seconds for the screen to be free, and what still matters then arrives as an ordinary Windows notification. While a game runs, clicks go straight through it; otherwise resting the mouse on it keeps it there and a click opens the flyout. Longer setup hints (a resolution that has to be created in NVIDIA Control Panel, say) are always ordinary notifications, and stay in the flyout's status card.

Text that changes while you watch rolls to its new value one character at a time: the countdown, the status card, a tile's **On** and **Off**, the number of games that are on. Only what changed moves, so `SDR in 3s` → `SDR in 2s` turns just the digit.

Turn it off with the **Island** tile; everything it would have said then comes as a notification, as in earlier versions. Movement follows the Windows setting **Accessibility › Visual effects › Animation effects**: with that off, the island and the flyout appear and change without animation. `HdrSwitch.exe --island-demo` shows every state in turn without switching anything, also next to a running copy.

The island's shapes and motion are inspired by Apple's Dynamic Island and by the open-source React project islandcn (MIT); no code is taken from it.

## Updates

Display Switch checks this repository once a day. When there is a new version, an **Update to …** button appears at the top of the flyout. One click downloads it, checks it against the release's SHA256 checksum, and restarts the app. The update check is the only time the app goes online.

## Uninstall

Turn off **Start with Windows**, choose **Exit**, then delete the folder with `HdrSwitch.exe` and `%AppData%\HdrSwitch`.

