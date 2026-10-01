@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app built from the `winui-tray-app` skill scaffold on top of `winapp new`. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- The app project is in `TrayCasts/`; run `winapp run` from there (or `dotnet run`), never register the package by hand. Use `winapp run --clean` to test first-run behavior. `TrayCasts.Core` (net10.0, no Windows APIs) holds the parser, search, storage, and policies; `dotnet test TrayCasts.Tests` covers it.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden. Anything it starts, it must stop in `FlyoutPage.OnHidden` / `Dispose`.
- Network work runs only while visible, through `Services/ForegroundPoller`. The one exception is background work the tray icon itself displays, and that needs a documented reason. Here that's playback: `Services/PlaybackService` keeps playing with no window, because the user started it and the tray menu, media keys, and overlay control it. It releases the `MediaPlayer` after 15 minutes paused. Every player callback must check that its player is still the live one (SMTC calls on a released player fail fast natively).
- The app needs no API keys today (search is the keyless iTunes API). Any future app key goes in `OAuth.resw` (gitignored) and is read through `Services/Secrets`. User tokens go in `Services/CredentialStore`. Nothing secret goes in `SettingsService`.
- The repo is MIT licensed (`LICENSE`). When the app's features are working, write a descriptive `README.md` (what it does, screenshot, install, setup/keys, privacy, build from source, license); the `winui-tray-app` skill lists the sections.
- Put pure decision logic (parsing, policies, formatting) in classes with no WinRT dependency, so a plain `net10.0` test project can cover it.
