# How it works

[← Back to the README](../README.md)


- **Steam games**: Steam marks a game as running in the registry the moment you press Play. Display Switch is told about that change by Windows, so HDR is on before the game even starts. That matters for games such as Dying Light: The Beast, which only offer their HDR option if Windows HDR is already on when they start.
- **Other games**: once a second it takes a lightweight list of running programs and compares it with your game list, catching games while they are still loading.
- When the last game closes, it waits 3 seconds and switches back to SDR.
- It only switches back HDR **it** turned on. HDR that you, a game or another tool turned on is left alone.
- Idle cost: about 0.2% of one CPU core and ~20 MB of memory.

Your game list lives in `%AppData%\HdrSwitch\games.txt` (one exe per line; a `-` in front means "never").

**If something goes wrong.** An unexpected error doesn't close the app or leave HDR, a competitive resolution or NVIDIA scaling switched on: it is written to `%AppData%\HdrSwitch\error.log` (the newest 256 KB at most) and the app carries on. If the app has to close because of one anyway, it first puts the displays back, as **Exit** does. If it is killed, crashes or the PC loses power mid-game, it picks up where it was the next time it starts: it keeps a note of what it switched in that folder (`hdr.state` lists the screens it turned HDR on for, `brightness.state` and `resolution.state` the SDR brightness and NVIDIA scaling to put back), naming each screen by the monitor and the port it is on, so the note still finds it after a reboot. HDR it turned on goes back to SDR once no game that wants it runs (after the usual wait), or when the game that is still running closes; HDR it didn't turn on still stays as it is. The SDR brightness and NVIDIA scaling go back on the screen they came from. The text files in that folder are saved by writing a copy beside them (`games.txt.tmp`) and then swapping it in, so a crash or power cut mid-save never leaves an empty `games.txt` or `resolutions.txt`. A file you made read-only is left alone (the change still applies until the app restarts), and lines the app doesn't understand in `resolutions.txt` and `brightness.txt` – notes, a line with a typo – are kept where they are when it saves.

## Building it yourself


Run `build.cmd` (or `build.cmd 1.2.0` for a versioned build). It uses the C# compiler that ships with Windows (.NET Framework 4.8).

`test.cmd` builds and runs the checks in `tests\` (parsing, the updater, rolling text, springs). GitHub runs both on every push, and a release is only published if the checks pass.

## Publishing a new version

Commit your changes, then tag and push:

```
git tag v1.2.0
git push origin main v1.2.0
```

GitHub Actions builds the exe, runs the checks and publishes the release; everyone's copy offers the update within a day. The tag is the version: it is built into the exe and written into the signed checksum file, so a release can't be moved to another tag afterwards (see below).

A release tag is `v` and three numbers, like `v1.2.3` (the exe's version is those numbers plus `.0`), and it must point at a commit on `main`. The release workflow checks both before it builds anything and stops otherwise, so a tag on another branch, or a name like `v1.2` or `v1.2.3-beta`, never becomes a signed release. Two tags pushed together are released one after the other, not at once.

### Signing

**Updates** are signed. The release workflow writes `SHA256SUMS.txt` with a version line and the exe's checksum:

```
# version 1.8.0
0123…cdef  HdrSwitch.exe
```

signs it with the private update key and publishes the signature as `SHA256SUMS.txt.sig`. The app has the public half built in (`Updater.PublicKeyXml`). The checksum shows the download arrived intact; the signature shows the checksum file came from the release workflow, not from a file changed or added to the release later; and the signed version line ties the file to its release. The tag isn't signed: without the line, someone able to edit releases (but without the key) could put an old signed build up under a tag like `v99.0.0`, and every copy would "update" to the old build, which would then find that release newer than itself again. The version is the tag without its `v`, as `build.cmd` gets it; `sha256sum -c` passes over the `#` line, and copies from before the line existed still find the exe's line, which is unchanged.

The app checks all of this once a day, before it offers anything, by fetching just `SHA256SUMS.txt` and its signature: the signature must be valid, the file must name a version, that version must be the release's tag and newer than the running copy, and it must have a checksum for the exe. A release that fails is never offered (the reason goes to `%AppData%\HdrSwitch\error.log`). Installing downloads all three files and checks everything again, the exe against its checksum as well, since a release's files can change between the check and the click. Releases signed before the version line existed don't carry it, so this copy never accepts them; that costs nothing, because it only ever looks at releases newer than itself, and those all carry the line.

Swapping in the new exe: the running exe is renamed to `HdrSwitch.old.exe`, the new one takes its name and starts with `--updated`, and the old copy exits. If the new exe can't take the name, the old one is put back (renamed, or copied if it can't be renamed) and the update reports the original error; whatever fails is logged. If the new exe is in place but won't start, the app says **Restart to finish** and the old copy keeps running until then.

An update waits while a game the app follows is running, while the countdown back to SDR or the saved resolution runs, and while Steam is starting a game. The copy that exits puts back everything it switched, so updating mid-game would turn HDR off and the resolution back under the game, and a game that only looks for HDR as it starts wouldn't get it back from the new copy. A click during a game downloads and checks the update right away and swaps it in once that is over. The private key is the `UPDATE_SIGNING_KEY` secret, RSA 3072 in .NET's XML form. Without it the release workflow stops before publishing, because no copy of the app would install that release. Keep a copy of the key somewhere safe: a release signed with a new key is only accepted by copies built with the new public key.

The workflow runs in three jobs so the keys never share a job with the repository's own code. `build` checks the tag, builds and runs the checks, with a read-only token and no secrets. `sign` checks out nothing and runs no file from the repository: it takes the exe `build` made, code-signs it (below), writes `SHA256SUMS.txt` and signs it; only this job sees `UPDATE_SIGNING_KEY` and the certificate, and its token has no permissions. `publish` creates the release from those files; it is the only job that can write to the repository, and it has no secrets. A change to the build, the checks or the app's code therefore can't read the key, only change the exe that gets signed, which is what review on `main` is for. The actions the workflows use are pinned to commit SHAs, so a moved tag in someone else's repository can't change what runs next to the key.

To protect the key further, put the `sign` job in a GitHub *environment* with required reviewers (add `environment: <name>` to it) and keep the secrets there; then only an approved tag push can sign.

**The exe** can be code-signed too (Authenticode). Windows then shows the publisher instead of "Windows protected your PC". The release workflow does this, in the `sign` job and before the checksum is taken, when the `SIGNING_CERT_PFX` (the certificate as base64) and `SIGNING_CERT_PASSWORD` secrets exist. A certificate comes from a certificate authority, from [SignPath Foundation](https://signpath.org) (free for open-source projects, after an application), or from Azure Trusted Signing. SmartScreen's warning only goes away once a signed exe has built up reputation.

