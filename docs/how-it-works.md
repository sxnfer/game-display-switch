# How it works

[← Back to the README](../README.md)


- **Steam games**: Steam marks a game as running in the registry the moment you press Play. Display Switch is told about that change by Windows, so HDR is on before the game even starts. That matters for games such as Dying Light: The Beast, which only offer their HDR option if Windows HDR is already on when they start.
- **Other games**: once a second it takes a lightweight list of running programs and compares it with your game list, catching games while they are still loading.
- When the last game closes, it waits 3 seconds and switches back to SDR.
- It only switches back HDR **it** turned on. HDR that you, a game or another tool turned on is left alone.
- Idle cost: about 0.2% of one CPU core and ~20 MB of memory.

Your game list lives in `%AppData%\HdrSwitch\games.txt` (one exe per line; a `-` in front means "never").

## Building it yourself


Run `build.cmd` (or `build.cmd 1.2.0` for a versioned build). It uses the C# compiler that ships with Windows (.NET Framework 4.8).

`test.cmd` builds and runs the checks in `tests\` (parsing, the updater, rolling text, springs). GitHub runs both on every push, and a release is only published if the checks pass.

## Publishing a new version

Commit your changes, then tag and push:

```
git tag v1.2.0
git push origin main v1.2.0
```

GitHub Actions builds the exe, runs the checks and publishes the release; everyone's copy offers the update within a day.

### Signing

**Updates** are signed. The release workflow signs `SHA256SUMS.txt` with the private update key and publishes the signature as `SHA256SUMS.txt.sig`. The app has the public half built in (`Updater.PublicKeyXml`) and only offers and installs a release whose checksum file carries a valid signature. The checksum shows the download arrived intact; the signature shows the checksum file came from the release workflow, not from a file changed or added to the release later. The private key is the `UPDATE_SIGNING_KEY` secret, RSA 3072 in .NET's XML form. Without it the release workflow stops before publishing, because no copy of the app would install that release. Keep a copy of the key somewhere safe: a release signed with a new key is only accepted by copies built with the new public key.

To protect the key further, put the release job in a GitHub *environment* with required reviewers and keep the secret there; then only an approved tag push can sign.

**The exe** can be code-signed too (Authenticode). Windows then shows the publisher instead of "Windows protected your PC". The release workflow does this when the `SIGNING_CERT_PFX` (the certificate as base64) and `SIGNING_CERT_PASSWORD` secrets exist. A certificate comes from a certificate authority, from [SignPath Foundation](https://signpath.org) (free for open-source projects, after an application), or from Azure Trusted Signing. SmartScreen's warning only goes away once a signed exe has built up reputation.

