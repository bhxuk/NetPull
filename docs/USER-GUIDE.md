# NetPull user guide

NetPull shows what's using your network: how fast, which app, who it's talking to, and how much you've used.

- [Installing](#installing)
- [Graph](#graph)
- [Usage](#usage)
- [Connections](#connections)
- [Devices](#devices)
- [Alerts](#alerts)
- [Mini graph and tray icon](#mini-graph-and-tray-icon)
- [Settings](#settings)
- [Questions](#questions)

## Installing

Download **NetPull-Setup.exe** from the [latest release](https://github.com/bhxuk/NetPull/releases/latest) and run it.
Windows asks for admin rights once. NetPull needs them to read Windows' own network events, which say which app sent or received each byte.

The installer sets up a startup task, so after that NetPull opens from the Start menu and at sign-in with no more prompts.

If Windows shows "Windows protected your PC", click **More info** and then **Run anyway**. The installer isn't code-signed.

For the first 10 minutes NetPull quietly learns the apps you already use, so you don't get a "new app" alert for each of them.

## Graph

The live graph shows the last 5 minutes, second by second. Download is cyan, upload is pink.

- **3 hours, 24 hours, Week, Month** switch to your history. Use the arrows to step back and forward, and **Now** to jump back.
- **Hover** the graph to read the speed at that moment.
- **Click** the graph to list the apps that were busy at that point. With nothing clicked, the list shows the busiest apps in the whole view.
- The cards above show the speed now (or the totals for the view), everything since NetPull started, and the busiest app.

## Usage

Totals for **Today, Yesterday, This week, This month, Last 30 days** or **Last 90 days**. Weeks start on Monday.

- **Apps** lists every app by how much it used. Click an app to see the hosts it talked to.
- **Hosts** lists websites and servers instead, with their country, ISP and which apps used them.
- **Export CSV** saves what's on screen.
- With a data cap set, a **Billing month** button appears and the cap card shows how much is left.

## Connections

Every open connection, refreshed every 2 seconds: the app, the host name (or address), country, ISP, port, TCP or UDP, state, speed now and total.

- Type in the search box to filter by app, host, country, ISP or port.
- **Active only** hides connections with no traffic in the last minute.
- **Show listening ports** adds apps waiting for incoming connections.
- Click a column heading to sort. Right-click a row to copy the address, look it up online, or show the app's file.

UDP doesn't really have connections, so UDP traffic seen in the last two minutes is listed instead.

## Devices

NetPull checks your home network every 5 minutes with quiet ARP requests (local network only, no port scanning).

- The first check learns what's already there, marks it trusted and doesn't alert.
- After that, anything new gets an alert and a **Not trusted** tag. Turn on **Trusted** once you know what it is.
- Click the pencil to give a device a name, or the bin to forget it.
- Phones and laptops that use a random ("private") Wi-Fi address can show up as new now and then. That's normal.

## Alerts

| Alert | When |
|---|---|
| **New app online** | An app uses the network for the first time. |
| **App changed** | An app's file is different from the last time it went online. Usually an update; worth a look if you weren't expecting one. |
| **Lots of data** | An app uses more than 500 MB in 10 minutes (you can change both). At most once an hour per app. |
| **Data cap** | You reach your warning level, and when you reach the cap. Off until you set a cap. |
| **New device** | Something new joins your home network. |

Alerts pop up as Windows notifications (click one to open NetPull) and stay on the Alerts page. Turn pop-ups off in Settings or from the tray menu.

## Mini graph and tray icon

The tray icon is a tiny graph of the last 10 seconds. Hover it for the speeds, click it to open NetPull, right-click it for the menu.

Turn on the **mini graph** from the tray menu or Settings. Drag it anywhere, double-click it to open NetPull, right-click it to keep it on top or hide it.

Closing the main window keeps NetPull running in the tray. Use **Exit NetPull** in the tray menu to stop it.

## Settings

- **Start with Windows** keeps NetPull recording from sign-in.
- **Speed units**: MB/s (what downloads show) or Mbps (what broadband is sold in).
- **Keep history for** 30 days, 90 days or 1 year.
- **Data cap**: your monthly allowance, the day your bill resets, when to warn you, and which adapter to count. If you use Tailscale, a VPN or virtual machines, pick your real internet adapter so traffic isn't counted twice.
- **Lookups**: countries and ISPs come from ip-api.com, device makers from macvendors.com. Turn either off and NetPull only uses your normal DNS for names.
- **Updates**: NetPull checks GitHub and can install new versions by itself.
- **Your data**: export every day's usage to CSV, open the data folder, or delete all history.

## Questions

**Does NetPull block anything?**
No. It only watches. It doesn't change your firewall or install a driver.

**Why do the totals look higher with "All adapters"?**
VPNs like Tailscale wrap your traffic: an app's traffic is counted on the Tailscale adapter, and the VPN app's own traffic carrying it is counted again on Ethernet or Wi-Fi. Pick one adapter at the top of a page to see just that one.

**Where's my data?**
In `%LOCALAPPDATA%\NetPull` on your PC. Nothing is uploaded. Only remote addresses go to the lookup services, and you can turn those off.

**NetPull says it isn't watching traffic.**
It needs admin rights. Open it from the Start menu (which uses the startup task), or reinstall to recreate the task. The log in the data folder (`netpull.log`) says what went wrong.
