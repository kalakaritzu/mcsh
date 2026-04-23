# McSH (aka. McShell)

![McSH Launcher](assets/banner.png)

**A fast, lightweight Minecraft launcher for the terminal.**

**Website:** https://mcsh.dev

McSH is a CLI Minecraft launcher built for people who want to manage Minecraft without the overhead of a GUI. No Electron, no splash screens, no background services. Just a shell prompt.

```
  __  __      ____  _   _
 |  \/  | ___/ ___|| | | |
 | |\/| |/ __\___ \| |_| |
 | |  | | (__ ___) |  _  |
 |_|  |_|\___|____/|_| |_|

v0.9.0  |  Type 'help' to begin
  Signed in as KALAKARITZU
  Tip: use Tab to autocomplete commands.

> instance create
> instance run
```

---

## Features

- **Instance management** — create, run, kill, rename, clone, delete, open folder, view info, per-instance config
- **Forge & NeoForge** — automatic installer, launch via full classpath
- **Fabric & Quilt** — automatic loader installation and profile resolution
- **Mod management** — search and install from Modrinth, toggle, update, remove, import `.jar`, named profiles
- **Modpack search & install** — search Modrinth, pick any version with a grouped version picker, installs the full `.mrpack` as a new instance
- **Modpack update** — `modpack update` checks all installed modpack instances for newer versions on Modrinth and updates in one command
- **Resource packs, shaders, plugins, datapacks** — same search/install/details flow via Modrinth
- **Microsoft authentication** — login, logout, status — full Device Code Flow, background token refresh, multi-account support
- **Java management** — `java` lists all detected installations; set a custom path globally with one command
- **Skin & cape system** — import local skin PNGs, apply them to your Minecraft account, select capes
- **Instance worlds** — `instance worlds` to browse, backup, delete, or open any world folder
- **Instance config** — per-instance window size, RAM, JVM args, environment variables, pre/post hooks
- **Auto-backup** — optionally backs up world saves automatically before every launch
- **Recent activity** — `recent` shows recently played worlds and servers across all instances
- **Quick CLI launch** — `mcsh run <name>` launches directly from any terminal, no REPL needed
- **Crash detection** — `instance crash` shows the latest crash report summary
- **Fast launch** — background pre-warm on `instance select`, differential cache to skip re-downloads, Aikar G1GC flags for better in-game performance
- **Tab completion** — context-aware completions for all commands and instance names
- **Themes** — six built-in color themes (Crimson, Arctic, Forest, Amber, Violet, Steel)
- **Localization** — English and Spanish; switch with `settings language <en|es>`
- **Update checker** — notifies on startup if a newer release is available

---

## Install

Download `McSH-0.9.0.msi` from the [latest release](../../releases/latest) and run it.

The installer registers `mcsh` on your PATH. Open any terminal and type `mcsh`.

**Requirements:** Windows 10/11 x64. No separate runtime needed — the installer is self-contained.

**Linux:** Download `McSH-0.9.0-linux-x64.tar.gz`, extract, and move `mcsh` to `/usr/local/bin/`.

---

## Quick Start

```
auth login               — sign in with your Microsoft account
instance create          — create a new instance (guided wizard)
instance select <name>   — set the active instance
instance run             — launch it
mcsh run <name>          — launch directly from any terminal (no REPL)

mod search sodium        — search Modrinth
mod install 1            — install by result number
mod remove <name>        — uninstall a mod

modpack search cobblemon      — search for modpacks
modpack install 1             — pick a version and install as a new instance
modpack update                — check all modpack instances for newer versions

datapack search <query>       — search Modrinth for data packs
shader search <query>         — search for shaders
resourcepack search <query>   — search for resource packs

skin import <path.png>        — import a skin PNG
skin                          — browse and apply saved skins
skin cape                     — select or disable a cape

java                          — list Java installations, set custom path

help                     — quick-start reference
ref                      — full command reference
```

---

## Command Reference

| Command | Description |
|---|---|
| `instance list` | List all instances with running status |
| `instance create` | Create a new instance (guided wizard) |
| `instance select <name>` | Set active instance and pre-warm |
| `instance run [name]` | Launch — prompts if none selected |
| `run <name>` | Quick CLI launch from terminal (alias: `r`) |
| `instance kill <name>` | Force-terminate |
| `instance clone <name>` | Duplicate an instance |
| `instance rename <name>` | Rename an instance |
| `instance delete <name>` | Delete an instance |
| `instance config [name]` | Configure window, RAM, JVM args, env vars, hooks |
| `instance worlds [name]` | Browse worlds — backup, delete, or open |
| `instance crash [name]` | Show the latest crash report |
| `instance export [name]` | Zip a full instance for sharing |
| `instance backup [name]` | Zip world saves with a timestamp |
| `instance import <path.mrpack>` | Import a Modrinth modpack |
| `instance mrpack` | Export instance as a shareable .mrpack |
| `instance prism <path>` | Import a Prism Launcher / MultiMC instance |
| `instance update [name]` | Update all mods to their latest versions |
| `modpack search <query>` | Search Modrinth for modpacks (alias: `mp`) |
| `modpack install <#\|slug>` | Install a modpack — version picker included |
| `modpack update` | Check installed modpacks for newer versions |
| `mod search <query>` | Search Modrinth for mods |
| `mod install <#\|slug>` | Install mod + dependencies |
| `mod remove <name>` | Uninstall a mod |
| `mod details <#\|slug>` | Full description, then install prompt |
| `mod toggle <name>` | Enable or disable without deleting |
| `mod import <path.jar>` | Import a local jar |
| `mod profile save/load/list/delete` | Manage named mod loadouts |
| `resourcepack / shader / plugin / datapack` | Same search, install, details, open flow |
| `skin [#]` | Browse saved skins and apply one |
| `skin import <path.png>` | Import a local skin PNG |
| `skin cape` | Select or disable a cape |
| `skin delete <name>` | Remove a saved skin |
| `java` | List detected Java installations, set custom path |
| `recent` | Show recently played worlds and servers |
| `recent run <#>` | Jump straight into a recent entry |
| `console` | Attach to running instance output (ESC to detach) |
| `auth login / logout / status` | Microsoft account management (alias: `a`) |
| `auth accounts / switch / remove` | Multi-account management |
| `settings` | Toggle UI, prompt, and behavior options (alias: `s`) |
| `settings theme <name>` | Switch color theme |
| `settings language <en\|es>` | Switch language |
| `ref` | Full command reference (alias: `c`) |
| `help` | Quick-start guide |
| `version` | Show McSH version and runtime info |
| `update` | Update McSH to the latest version |
| `restart` | Restart McSH in a new terminal window |

---

## Building from Source

```
git clone https://github.com/kalakaritzu/mcsh
cd mcsh
dotnet build
dotnet run
```

To build the Windows installer:

```
build-installer.bat
```

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download) and the [WiX toolset](https://wixtoolset.org) (`dotnet tool install --global wix`).

---

## Notes

- Data is stored in `%APPDATA%\McSH\` on Windows, `~/.local/share/McSH` on Linux
- Skins are stored locally in the McSH data directory and applied to your Minecraft account via the Mojang API

---

## License

MIT
