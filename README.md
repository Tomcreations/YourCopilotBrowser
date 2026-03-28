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

## Diagnostic Data

YCB Browser collects diagnostic data by default to help us find and fix bugs. Here is exactly what is and is not logged:

**What we do log:**
- App launch info (app version, WebView2 version, .NET version, Windows version)
- Tab open and close counts
- Navigation errors (domain only, never the full URL)
- Download outcomes (file type and size only, never filenames or content)
- Password prompt shown/saved events (domain only, never your actual passwords)
- UI freezes and crash reports

**What we never log:**
- URLs you visit
- Your browsing history
- Passwords or any credentials
- File names or file content
- Any personally identifiable information

**Why we do this:**

Early on we had a bug where YCB worked perfectly on one machine but crashed on startup on another. We had no idea why — no error, no clue. Once we added the error logger we could see exactly what was different (a missing WebView2 version) and fix it in minutes. Without it we would have been completely in the dark. That one case alone is why this exists.

You can turn this off at any time in **Settings > Data & Privacy**.

## Support & Your User ID

Every install of YCB has a unique **User ID** which you can find in **Settings > About**. This ID is anonymous and is only used to match your error logs if you contact support.

If you are having an issue, open **Settings > About**, copy your User ID, and include it when reaching out to support. With it we can look up your specific error logs and tell you whether the problem is on your end or ours — and how to fix it either way.
