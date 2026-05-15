# YCB Browser

YCB Browser is a Windows desktop browser built with WPF, .NET 8, and Microsoft WebView2.

This repository is the clean source version of the app. It is meant for reading the code, understanding how the browser works, or building your own local copy without using the public installer.

## What is included

- WPF browser shell and tab UI.
- WebView2 browser integration.
- Profile chooser and profile settings.
- Internal pages for new tab, settings, downloads, history, extensions, profiles, passwords, support, and guide.
- Optional supported AI services configured from settings.
- Chrome-style extension management where WebView2 supports it.
- uBlock Origin extension assets used by the built-in ad blocker option.
- Bitwarden-oriented password manager UI and autofill integration code.
- Cookie import and per-profile browser data logic.
- Diagnostics and startup error reporting code.

## What is not included

- Compiled app binaries.
- Published build folders.
- Installer EXE or ZIP files.
- Local user profiles, cookies, cache, logs, screenshots, or test output.
- Generated downloader/server binaries.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 SDK.
- Microsoft WebView2 Runtime.

The WebView2 NuGet package is restored automatically by `dotnet restore`.

## Build

From the repository root:

```powershell
dotnet restore
dotnet build -c Release
```

To publish a local self-contained build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
```

The public installer is distributed separately from the website. This source repository is for building or inspecting the browser itself.

## Notes

YCB stores per-user browser data outside the source tree. Different YCB profiles are designed to keep cookies, history, settings, extensions, and sessions separate.

Optional AI support works with supported AI services selected in settings. YCB does not include an AI model and does not bill for AI access.
