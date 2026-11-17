<div align="center">

<img src="docs/images/icon.png" width="96" alt="NetPull icon">

# NetPull

**See what's using your network.**

A live graph of everything going in and out of your PC, which app is using it, who each app is talking to,
how much you've used this month, and alerts when something new turns up. For Windows 10 and 11.

**[Download NetPull for Windows](https://github.com/bhxuk/NetPull/releases/latest/download/NetPull-Setup.exe)**  ·  [Website](https://bhxuk.github.io/NetPull/)  ·  [User guide](docs/USER-GUIDE.md)  ·  [What's new](CHANGELOG.md)

</div>

---

## What's inside

| Page | What it shows |
|---|---|
| **Graph** | A live, glowing download and upload graph. Switch to 3 hours, 24 hours, a week or a month and step back through your history. Click any point to see which apps were busy right then. |
| **Usage** | How much each app used today, yesterday, this week, this month, or over the last 30 or 90 days. Click an app to see who it talked to. Switch to **Hosts** to see it by website or server instead. |
| **Connections** | Every open connection: the app, the host name, country and ISP, port, state and live speed. Search, sort, and right-click to copy or look up an address. |
| **Network** | A map of your whole network: Internet, router, switches and access points, and every device on each, plus your Tailscale machines. Click a device to see what it is, which services it runs, and (with UniFi) how much data it uses and what it's doing. |
| **Alerts** | An app going online for the first time, an app's file changing since it last went online, an app or device using lots of data, data caps, new devices, and devices going offline or coming back. |
| **Settings** | Start with Windows, the mini graph, speed units, alert options, a monthly data cap, lookups, updates and your data. |

And across the app:

- **Whole network graph**: switch the Graph to **All devices** to see your whole network's traffic and the busiest devices (needs UniFi).
- **Adapter filter**: show everything, or just Ethernet, Wi-Fi, Tailscale or a VPN.
- **Mini graph**: a small floating speed graph you can drag anywhere.
- **Live tray icon**: the icon itself is a tiny graph. Hover it for the speeds.
- **Export to CSV** from Usage, Connections and Settings.
- **Automatic updates** from this page's releases.

## Install

1. Download **[NetPull-Setup.exe](https://github.com/bhxuk/NetPull/releases/latest/download/NetPull-Setup.exe)** and run it.
2. Windows asks for admin rights once. NetPull needs them to read Windows' own network events, which is how it knows which app sent or received each byte.
3. That's it. NetPull starts in the tray when you sign in, with no more prompts.

Windows may show "Windows protected your PC" because the installer isn't code-signed. Click **More info** and then **Run anyway**.

## How it works

- Traffic per app comes from Windows' built-in network event tracing (ETW). No driver is installed and nothing is changed in your firewall.
- History is added up every minute and kept in a small database in `%LOCALAPPDATA%\NetPull`. Minute detail is kept for a week, hourly totals for 90 days (or 30 days, or a year, in Settings).
- Host names come from your normal DNS. Countries and ISPs come from [ip-api.com](https://ip-api.com) and device makers from [macvendors.com](https://macvendors.com). Both can be switched off in Settings, and only remote addresses are sent, never your traffic.
- Device checks are ARP requests on your local network only. To name devices, NetPull tries to connect to a few common ports (web page, SSH, Plex, cameras, printers); it never sends anything.
- A PC can't see other devices' traffic on its own. If you use a UniFi console, NetPull can read each device's data, Wi-Fi details and app categories from it with a **View Only** local user. It only reads; the password is encrypted with your Windows account.
- Tailscale machines come from `tailscale status` on this PC.

## Build it yourself

You need the .NET 8 SDK on Windows.

```
dotnet publish src/NetPull/NetPull.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The installer is built with [Inno Setup 6](https://jrsoftware.org/isinfo.php) from `installer/NetPull.iss`. GitHub Actions builds both on every push and publishes a release when the version in `NetPull.csproj` changes.

## Licence

MIT
