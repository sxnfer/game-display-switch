# Recording and streaming

[← Back to the README](../README.md)


An HDR game recorded by OBS with its usual SDR settings comes out washed out: OBS has to squeeze the HDR picture into SDR. Turn on **SDR while recording** in the flyout and, while a recording or streaming app runs (OBS Studio, Streamlabs, XSplit, PRISM Live Studio, Twitch Studio, vMix or Bandicam), nothing switches HDR on, so your recordings keep their normal colours. If a game already has HDR on when OBS starts, HDR goes back to SDR straight away; when OBS closes and the game is still running, HDR comes back on. HDR you turned on yourself is left alone. It is off until you turn it on.

Some games only offer their HDR option if HDR was on when they started (Dying Light: The Beast, for example), so while OBS is open they run in SDR until you restart them. Want HDR footage instead? Leave the tile off and set OBS to record in HDR: **Settings › Advanced**, Color Format **P010** and Color Space **Rec. 2100 (PQ)**, with a 10-bit HEVC or AV1 encoder. Those files only look right on an HDR screen or YouTube HDR.

## Other recording apps

Use something else? Add its exe to `%AppData%\HdrSwitch\recorders.txt`, one per line, with the name to show after a `#`:

```
recorder.exe   # My recorder
```

Only add an app you open when you want to record or stream. The NVIDIA overlay (ShadowPlay), Xbox Game Bar, Discord and Medal usually run in the background all the time, so with one of them on the list HDR would never come on. They are not on the built-in list for that reason.
