# YCB Browser

A fast, clean browser built on WebView2 with Copilot integration.

---

## Install

Download the latest installer at **[ycb.tomcreations.org](https://ycb.tomcreations.org)**

## Features

- Built-in GitHub Copilot CLI integration
- Password manager with encryption
- Incognito mode
- Bookmarks & history
- Chrome-style permission prompts (camera, mic, location)
- Per-site settings & cookie controls
- Download manager
- Dark UI

## Requirements

- Windows 10/11
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (installed automatically)
- [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## AI Features (Optional)

The built-in AI assistant uses **GitHub Copilot CLI**. This is completely optional — the browser works fully without it.

To enable AI:

1. Install GitHub Copilot CLI via winget:
   ```
   winget install GitHub.Copilot
   ```
2. Log in:
   ```
   copilot login
   ```
3. A valid **GitHub Copilot subscription** is required.

> **Note:** We do not host, provide, or charge for the AI. The AI is powered by GitHub Copilot, a service operated and billed exclusively by **GitHub (a Microsoft company)**. Any subscription or billing queries should be directed to GitHub.
