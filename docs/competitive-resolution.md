# Competitive resolution

[← Back to the README](../README.md)


Some people play shooters on a smaller area than their monitor, say 25" of a 27" panel, with a low stretched resolution inside it. Display Switch can do that per game: while the game runs, the desktop switches to a resolution of your choice, shown **1:1 in the middle of the screen with black borders**, so the monitor behaves like a smaller one. When the game closes, the screen goes back to its normal resolution. It is independent of HDR: a game can have HDR, a resolution, both or neither, and a game you set to "never HDR" can still have a resolution.

In the flyout's **Games** tab, click a game to open its page and turn on **Competitive area**. The list that opens shows **Off** (the whole screen), then every resolution your display offers at its current refresh rate, the ones with the screen's own aspect ratio first, each with how much of the screen it covers (for example `92.5 % · 24.6″`). Pick one and it is saved, and applied right away if the game is running. Back on the page, the card shows your screen to scale with the area drawn 1:1 in the middle, its size and diagonal (`2368 × 1332 · 24.5″`), and the size with **More…** to pick another and **Create…** to make one the display doesn't have yet; the switch turns it off again. If the display doesn't offer that size at the rate it switches at, a yellow bar on the page says what to create. The flyout's **Live** tab shows the active resolution as the **Competitive area** layer (and a stretch as **Stretch**), and the tray icon's tooltip shows it next to the HDR state, for example `2368 × 1332 – Counter-Strike 2`.

## Creating the resolution

The resolution has to exist before Display Switch can switch to it: Windows only switches to sizes the monitor is known to have. Display Switch can add one for you.

**Create a size.** On the game's page, click **Create…** on the Competitive area card (or **Create a size…** under the list of sizes). Then:

1. **Screen size**: drag the slider to the diagonal you want, in 0.1″ steps from 15″ up to your panel's own size (the arrow keys move a tenth, Page Up and Page Down a whole inch). Display Switch reads the panel's size from the monitor itself.
2. **Shape**: your screen's own (*Native*), 16:9, 16:10, 4:3 or 5:4.
3. **Refresh rate**: your desktop's current rate, or another one the monitor has at its full resolution. A resolution the GPU scales can only use a rate the monitor has at native.

Above the controls, the picture shows the size to scale on your screen, and the line under it what it comes to, for example `2368 × 1332 · 24.51″ · 92.5 %`: the largest resolution of that shape that still reads as the diagonal you chose, with a width that is a multiple of 8. If the display already has that size, the button says **Use it** and nothing has to be created. Otherwise **Create…** shows what is about to happen, and **Create and restart driver** does it. A few seconds later the game has its new size.

**It needs CRU, once.** Display Switch adds the resolution the way [Custom Resolution Utility (CRU)](https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU) by ToastyX does, and uses CRU's own `restart64.exe` to restart the graphics driver. CRU is free; Display Switch neither includes nor downloads it. Download it from [its page](https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU), unzip it anywhere, and choose that folder when the creator asks (**Open download page** and **Choose folder…**), or in **Settings › Resolutions › CRU folder**. The folder has to contain `CRU.exe` and `restart64.exe` (`restart.exe` on 32-bit Windows). You never have to open CRU itself. This also gets round NVIDIA Control Panel's limits on custom resolutions.

**What it does to your PC, exactly.** A monitor tells Windows which resolutions it has in a small block of data, its EDID. Windows lets that be overridden in the registry, under `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<monitor>\<instance>\Device Parameters\EDID_OVERRIDE`. That is where CRU saves its changes, and where Display Switch adds the one resolution. The monitor itself is never written to. Then the graphics driver restarts so it reads the override:

- **Your screens go black for a few seconds** while the driver restarts. Windows and your programs keep running; some windows may move. Display Switch won't start this while a game is running.
- **If the picture doesn't come back within 15 seconds, press F8.** That is CRU's recovery mode, which temporarily unloads the overrides without deleting them. Then use **Remove** (below).
- **Windows asks for administrator permission**, because the override is in a protected part of the registry. Display Switch itself keeps running without those rights: only this one step is done by a second, short-lived copy of it. Say no at the prompt and nothing is changed.
- **Monitors that use DSC** (Display Stream Compression: many 4K 144 Hz+ and 1440p 360 Hz+ monitors) can make the NVIDIA driver ignore EDID overrides. Then nothing changes: Display Switch checks afterwards that Windows really lists the new resolution, and tells you if it doesn't.

**What keeps it safe.**

- **A backup first.** Before it writes to a monitor's override for the first time, Display Switch saves what was there (whether an override existed, and every byte of it) to `%AppData%\HdrSwitch\edid-backups\`. Nothing is written if the backup can't be saved and read back.
- **Your own CRU changes stay.** If the monitor already has an override, the new resolution is added to it. An override Display Switch can't read is never written over: it tells you, and you sort it out in CRU.
- **Nothing changes behind your back.** If the monitor's EDID or override changed between the confirmation and the write (you used CRU meanwhile, say), nothing is written and you are asked to open the creator again.
- **The write is checked.** What was written is read back; if writing fails half way, what was there before is put back at once.
- **Everything is in the log.** Each step is a line in **Settings › Switch log** (`edid-apply`, `edid-backup`, `edid-write`, `edid-restart`, …), with what the EDID gained.

**Undo.** **Settings › Resolutions** lists each monitor Display Switch added resolutions to. **Remove** puts that monitor's override back exactly as the backup has it (your own override from before, or no override at all), restarts the driver, and deletes the backup. It removes everything Display Switch added to that monitor, and any change made to the override since (in CRU, for example). A game whose size is gone then says so on its page, and switches again once you pick or create another. You can also remove an override in CRU itself (**Delete**, then `restart64.exe`), or with CRU's `reset-all.exe`.

**Or create it yourself in NVIDIA Control Panel.** If you'd rather not use CRU, and NVIDIA Control Panel accepts the resolution:

1. **Display › Change resolution › Customize… › Create Custom Resolution**. Enter the size you want, at your monitor's refresh rate (not 60 Hz), test and save it. For 25" on a 27" 2560×1440 panel that is **2368 × 1332** (exactly 16:9, 92.5 % of the diagonal).

## Scaling

In NVIDIA Control Panel, **Display › Adjust desktop size and position**: keep the scaling you like for everything else, for example **Full screen**, so other games can stretch a low resolution over the whole screen. Perform scaling on **GPU**, and leave **"Override the scaling mode set by games and programs" unticked**.

While a game with a competitive resolution runs, Display Switch switches NVIDIA's scaling to **No scaling** itself and puts your setting back when the game closes. That is what keeps the picture centred when the game uses *exclusive* fullscreen, which sets its own display mode. The driver saves this setting like Control Panel does, so if the PC loses power mid-game, Display Switch puts it back the next time it starts. It only puts back a No scaling it set: if you change the setting yourself while the game runs, it leaves yours alone. On AMD or Intel graphics this step is skipped.

## While you play

In the game, use the **same resolution as the competitive one** in exclusive fullscreen, or borderless (fullscreen windowed) mode, which always draws at the desktop's size and so fills the area. For a lower resolution stretched over the area, see the next part.

The switch happens the moment you press Play in Steam, or when the game's process appears, so the game sees the smaller desktop from the start. If you set a resolution while the game is already running, restart the game if it doesn't fit. Three seconds after the last game with a resolution closes, the display goes back to the mode saved in Windows display settings, scaling included. The change is never saved: a reboot or sign-out always comes back at your normal resolution, and if Display Switch is killed mid-game, Windows drops the temporary mode with it (the next start switches again if the game is still running). As with HDR, it only undoes what it did: if you or Windows change the resolution while the game runs (display settings, sleep and resume), it leaves it alone.

**A stretched resolution inside the area.** A game can also run at a *lower* resolution of its own, stretched over the competitive one: 4:3 at 1776 × 1332 across a 2368 × 1332 area, say. On the game's page, click **Stretch** (under **Competitive area**, which it needs) and pick one; the list shows what fits inside the competitive resolution, and the picture of the screen then shows the stretched resolution at its own size inside the area. While the game runs, the desktop switches to that resolution and the GPU stretches it over the area, with the same black borders around it. The monitor still gets its native signal, and the stretching is done by the scaler in the GPU's display output, the one "Full screen" scaling uses, only aimed at a smaller rectangle. Nothing is added to the display path. Three things follow from how it works:

- **The game has to be in borderless mode** (*Fullscreen Windowed* in Counter-Strike 2, *Windowed Fullscreen* in Valorant). There it draws at the desktop's resolution and asks Windows for nothing. A game in *exclusive* fullscreen sets a display mode of its own, and Windows then goes back to the driver's scaling: a 1:1 box or the whole panel. Display Switch doesn't fight over the display: it leaves the game's mode alone and says so, in a notification and on the **Stretch** layer of the flyout's **Live** tab (*The game set its own mode since*). Switch the game to borderless and start it again. A borderless game that covers the screen is normally sent straight to the display on Windows 11, as exclusive fullscreen is; anything drawn over it (a notification, an overlay) goes through Windows' compositor while it shows.
- **The resolution has to fit inside the competitive one.** The GPU's scaler only enlarges. A resolution that would have to shrink (1920 × 1440 into 2368 × 1332) is done by Windows' compositor instead, which costs latency, so it is neither offered nor applied. The largest 4:3 inside 2368 × 1332 is 1776 × 1332.
- **Everything is stretched while the game runs**, the desktop and the windows you Alt+Tab to included, because the desktop itself is at the game's resolution.

Unlike the plain competitive resolution, Windows doesn't drop this mode by itself if Display Switch is killed mid-game: Display Switch puts the screen back the next time it starts, and so does a reboot or sign-out, since the mode is never saved. NVIDIA's scaling setting isn't touched for these games. This was developed and tested on an NVIDIA card; another graphics driver may refuse it, in which case nothing is switched and a notification says so ([#1](https://github.com/sxnfer/game-display-switch/issues/1)).

The profiles live in `%AppData%\HdrSwitch\resolutions.txt`, one game per line (`cs2.exe  2368x1332`, optionally `@240` for a refresh rate and `stretch 1776x1332` for the game's own resolution); the pencil on the game's page opens it, and edits apply as soon as you save. If a resolution doesn't exist at the needed refresh rate, nothing is switched and a notification says what to create ([Creating the resolution](#creating-the-resolution)).

**If a game starts at the wrong size** (full screen at your normal resolution, a box that isn't the size you chose, a stretch that didn't happen), open the flyout's **Settings › Switch log**. Each decision is one line: look for the `apply` line at the time the game started, which names the mode wanted, the mode that was there and why it was or wasn't switched, then `scaling-take` (NVIDIA's scaling), `picture` (whether the result is centred or stretched) and any `display-changed` line while the game ran. [Using it](using-it.md#the-flyout) says what each line means.
