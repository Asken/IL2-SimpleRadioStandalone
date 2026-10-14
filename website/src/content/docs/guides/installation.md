---
title: Installation
description: Install one copy of SRS for IL-2 Great Battles and IL-2 Korea.
---

## Before you begin

You need Windows, IL-2 Great Battles and/or IL-2 Korea, and access to an SRS server used by your multiplayer server.

:::caution[Close IL-2 before installing]
Close IL-2 Great Battles and IL-2 Korea before running the SRS installer, including any dedicated server instance. The installer may need to update each game's `startup.cfg`; a running game can prevent the repair or overwrite the changes when it exits. You can restart IL-2 after installation is complete.
:::

:::tip[Install SRS only once]
One SRS installation supports both games. Do not install SRS inside either game folder and do not create separate Great Battles and Korea copies.
:::

## Install the stable release

1. Download the [latest stable Auto Updater](https://github.com/riaanjutte/IL2-SimpleRadioStandalone/releases/latest/download/IL2-SRS-AutoUpdater.exe).
2. Run the updater and install to the recommended application folder:

   ```text
   C:\Program Files\IL2-SimpleRadio-Standalone
   ```

3. Allow the installer to configure telemetry for every detected IL-2 installation.
4. Start SRS and select your microphone and speakers.
5. Open the **Controls** tab and bind at least one Push-To-Talk control.

SRS stores user configuration separately in `%AppData%\IL2-SRS`, so updates or application-folder changes do not separate the client from its profiles and bindings.

## Existing or duplicate installations

The installer can consolidate older duplicate SRS installations. It preserves profiles, bindings, favourites, and radio presets, and creates a migration backup before retiring duplicate program files.

Use the recommended Program Files installation as the copy you keep. Do not manually merge configuration files unless consolidation reports a conflict.

## Upgrading from the original IL2-SRS 1.0.2.0

The original 1.0.2.0 updater cannot update to this version, so download and run the updater above. When the installer finds a 1.0.2.0 (or earlier) installation, it explains the upgrade before it starts:

- Your key bindings, profiles, audio devices and volumes, favourite servers, radio presets and window positions are copied to `%AppData%\IL2-SRS`, with a backup of the old files under `MigrationBackups`.
- SRS starts with the **Classic** theme, which looks like 1.0.2.0, and in English. You can change both later under **Settings**.
- On first start, SRS offers the community's recommended radio settings. Choose **No** to keep your current settings exactly as they are.

This only happens the first time: if you have already used a newer version on this PC, your existing settings are kept unchanged.

## First launch

On startup, SRS checks the telemetry configuration for both games if they are installed. If a required `startup.cfg` change cannot be made, the client displays a warning instead of silently continuing.

If IL-2 is already running, close the game before applying telemetry repairs. IL-2 can rewrite `startup.cfg` while it exits.

## Installing a beta

Open the [Releases page](https://github.com/riaanjutte/IL2-SimpleRadioStandalone/releases), choose the newest release marked **Pre-release**, and use the updater attached to that release. Review the release notes before installing.

For later upgrades, beta-to-stable changes, reinstallations, backups, and clean resets, see [Updates and recovery](../update-recovery/).

## Next step

Continue with the [Quick start](../quick-start/) guide.
