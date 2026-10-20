---
title: Web server (Docker and Windows service)
description: Run the IL2-SRS server in Docker or as a Windows service, managed from the browser and a REST API.
---

The web server is the IL2-SRS server as a .NET 10 application with a browser-based admin UI and a REST API. Existing IL2-SRS clients connect to it exactly as they do to the Windows server window; nothing changes for players.

Use it when you want to:

- run SRS on a Linux host or alongside other containers;
- run SRS as a Windows service without a desktop session;
- manage the server from a browser, or automate it with scripts and bots.

:::note[Preview]
The web server is new. The Windows server window remains available while the web server is proven in production.
:::

## What you get

- **Dashboard** with listener health, uptime, clients, voice links, recent transmitters and recent activity.
- **Clients**: search, mute, kick, and ban with a reason and optional duration.
- **Settings** and **Channel Names**, applied immediately and sent to connected clients. Channel names can be shared, or set separately for Allies and Axis.
- **Bans** with reason, who banned, and expiry. Ban an IP address directly or remove a ban.
- **Event log** of logins, admin actions, server start and stop, client connections and refused connections, name and coalition changes and radio retuning. It updates live; filter it by category, type or client, and click an event to see its details. Old events are removed automatically.
- **Integrations** that post to Discord or call a web address when something happens on the server, for example a pilot joining or the server starting. See [Integrations](#integrations).
- **API keys** for the REST API, with read-only or read-write access, and an interactive **API reference**.
- **Plugins** that add pages, API endpoints and event handling. See [Plugins](#plugins).

Times are shown in your own time zone. The **Desktop / Light / Dark / IL-2 SRS** button in the top bar switches the theme; **Desktop** follows your system setting. The choice is remembered in each browser. On a phone, the menu is a dropdown at the top of the page.

Settings, bans, the event log and API keys are stored in `srs.db`, a SQLite database in the server's data folder. Plugins keep their own data under `plugin-data` in the same folder.

## Run in Docker

The image is published on Docker Hub as [`asken/il2-srs-server`](https://hub.docker.com/r/asken/il2-srs-server). Start it with an admin password:

```sh
docker run -d --name il2-srs-server --restart unless-stopped \
  -e SRS_ADMIN_PASSWORD='a-long-password' \
  -p 6002:6002/tcp -p 6002:6002/udp -p 127.0.0.1:8080:8080 \
  -v il2-srs-data:/data asken/il2-srs-server:preview
```

This publishes the SRS port `6002` for TCP and UDP, keeps the admin UI on `http://127.0.0.1:8080`, and stores data in a named volume. The repository also contains a `docker-compose.yml` with the same setup, and a `Dockerfile` if you prefer to build the image yourself.

`docker ps` shows the container as `healthy` while both SRS listeners are running. It turns `unhealthy` if a listener fails, or while an admin has stopped the server from the dashboard.

:::caution[Protect the admin UI]
The admin UI and API use plain HTTP. Keep port 8080 on localhost, or put a reverse proxy with HTTPS (Caddy, Traefik, nginx) in front of it before exposing it to the internet.
:::

## Run as a Windows service

1. Get a self-contained build, which needs no .NET installation on the server. Download `IL2-SRS-Server-Web-<version>-win-x64.zip` from [GitHub Releases](https://github.com/riaanjutte/IL2-SimpleRadioStandalone/releases) (from 1.0.5.0-beta.1) and extract it to `C:\IL2-SRS\app`, or publish one yourself:

   ```powershell
   dotnet publish IL2-SRS-Server-Web -c Release -r win-x64 --self-contained -o C:\IL2-SRS\app
   ```

   The build includes the Integrations plugin, in `plugins\IL2-SRS-Plugin-Integrations`.

   Windows marks files extracted from a downloaded ZIP as coming from the internet. If PowerShell refuses to run the install script, unblock the folder first:

   ```powershell
   Get-ChildItem C:\IL2-SRS\app -Recurse | Unblock-File
   ```

2. From an elevated PowerShell prompt, install and start the service:

   ```powershell
   C:\IL2-SRS\app\Install-WindowsService.ps1 -DataDirectory C:\IL2-SRS\main
   ```

   The script asks for the admin password, writes it to `srs-server.json` in the data folder (readable only by Administrators and SYSTEM), adds firewall rules for the SRS port, and sets the service to restart after a crash.

3. Open `http://localhost:8080` on the server and log in.

To test without a service, run `IL2-SRS-Server.exe --data-dir C:\IL2-SRS\main` from a console with `SRS_ADMIN_PASSWORD` set. Without `--data-dir`, the server keeps its data next to the exe, or in `C:\ProgramData\IL2-SRS-Server` when that folder is not writable (for example under `C:\Program Files`).

## Move an existing server

Point the data folder at a copy of your existing server folder. On first start, the web server imports `server.cfg` and `banned.txt` into `srs.db`, so settings, channel names and bans carry over. The original files are not changed. After the import, change settings in the admin UI; edits to `server.cfg` are no longer read.

## Configuration

Set these as environment variables, as `--KEY=value` arguments, or in `srs-server.json` in the data folder:

| Key | Meaning |
| --- | --- |
| `SRS_ADMIN_PASSWORD` | Admin password, at least 8 characters. Required. |
| `SRS_ADMIN_PASSWORD_FILE` | Read the password from a file (Docker secrets). |
| `SRS_<SETTING>` | Fix any [server setting](../configuration-reference/), for example `SRS_SERVER_PORT=6002`. Fixed settings are shown as locked in the admin UI. |
| `SRS_EVENT_RETENTION_DAYS` | Keep events for this many days (default 30). |
| `SRS_EVENT_MAX_ROWS` | Keep at most this many events (default 100000). |
| `urls` | Admin UI address. Defaults to `http://localhost:8080` on Windows and port 8080 on all interfaces in Docker. |

Run several servers by giving each its own data folder and SRS port, and on Windows its own service name and admin UI address.

## REST API

Create a key under **API Keys** in the admin UI and send it as `Authorization: Bearer <key>`:

```sh
curl -H "Authorization: Bearer $SRS_API_KEY" http://localhost:8080/api/v1/clients
```

Read keys can view status, clients, settings, channel names, bans, events, plugins and integrations. Write keys can also change settings, kick, ban and mute clients, manage bans and integrations, clear the event log, and start, stop or restart the server. Every change made with a key is recorded in the event log under the key's name.

Each event has a type, such as `client.connected`, and details as JSON. `GET /api/v1/events` filters by type and client, and `GET /api/v1/event-types` lists the types and their fields.

The **API reference** button on the API Keys page opens an interactive reference (`/scalar`) where you can paste a key and try requests from the browser. Scripts and code generators can use the OpenAPI document at `/openapi/v1.json`.

## Integrations

An integration runs an action when one of its events happens, for example **Server started** or **Client connected**. Add conditions to narrow it down, for example only pilots on the Red side. The built-in actions are:

- **Post to Discord** through a channel webhook, as a plain message or a card. It can post a new message for each event, or keep updating one message, for example a server status pinned in the channel.
- **Send an HTTP request** to any web address, for example a Slack or Teams webhook or your own bot.

Messages are templates filled in from the event, such as `{{ client.name }} joined {{ client.coalitionName }}`, with a live preview on the integration page. **Test** runs the integration once with example data.

Runs are queued, kept across restarts and retried if the integration allows it; **Runs** on the Integrations page lists them. Webhook addresses and headers are stored encrypted with the keys in the `keys` folder and are never shown again, so back up `keys` together with the data.

## Plugins

Integrations are a plugin, included in the Docker image and the Windows build. The **Plugins** page shows the plugins the server runs, and why a plugin did not load.

To add a plugin, copy its folder into `plugins` in the data folder and restart the server. Plugins run inside the server with its permissions, so only install plugins you trust. For writing your own, see [Plugins in the web server README](https://github.com/riaanjutte/IL2-SimpleRadioStandalone/blob/master/IL2-SRS-Server-Web/README.md#plugins).

## Differences from the server window

- UPnP port forwarding is not supported. Forward the SRS port (TCP and UDP) on your router or host.
- The update checker is not included. Update the container image or the published files instead.
- The server-wide White and Dark theme is replaced by the per-browser Desktop / Light / Dark button.
