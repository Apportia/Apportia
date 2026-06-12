# Apportia

<p align="center">
<img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logoColor=white" title=".NET 10 or higher" alt=".NET">
<img src="https://img.shields.io/badge/language-C%23-239120?style=for-the-badge&logo=csharp&logoColor=white" title="Written in C#" alt="C#">
<img src="https://img.shields.io/badge/UI-Avalonia-8B5CF6?style=for-the-badge&logoColor=white" title="Built with Avalonia UI" alt="Avalonia">
<img src="https://img.shields.io/badge/cross%E2%80%93platform-Linux%2BWindows-blue?style=for-the-badge&logo=linux&logoColor=silver" title="Runs on Linux and Windows" alt="Platform">
<a href="LICENSE.txt"><img src="https://img.shields.io/github/license/Apportia/Apportia?style=for-the-badge" title="Read the license terms" alt="License"></a>
</p>
<p align="center">
<a href="../../actions/workflows/build.yaml"><img src="https://img.shields.io/github/actions/workflow/status/Apportia/Apportia/build.yaml?label=build&logo=github&logoColor=silver&style=for-the-badge" title="Check the last workflow results" alt="Build"></a>
<a href="../../issues"><img src="https://img.shields.io/github/issues/Apportia/Apportia?logo=github&logoColor=silver&style=for-the-badge" title="Browse open issues" alt="Open Issues"></a>
<a href="../../commits/main"><img src="https://img.shields.io/github/last-commit/Apportia/Apportia?logo=github&logoColor=silver&style=for-the-badge" title="Check the last commits" alt="Last Commit"></a>
<a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/Apportia/Apportia?logo=github&logoColor=silver&style=for-the-badge" title="Check the latest release" alt="Release"></a>
</p>

**Apportia** is a cross-platform manager for portable Windows applications. It lets you browse, install, and launch hundreds of portable apps on both Windows and Linux — with Wine handling execution on Linux transparently.

> Apportia derives from *apport* — to bring forth — with a nod to *aporia*, the philosophical state of contradiction. Because that's exactly what portable apps are: software that was never meant to be portable, forced into harmony.

---

## Preview
<p align="center"><img src="preview.png"></p>

---

## Features

- Browse and search the full [PortableApps.com](https://portableapps.com) catalogue — support for additional app sources planned
- Apps install and update silently in the background — no wizards, no popups
- Passive update detection without interruptions
- Runs natively on Linux and Windows; portable apps are executed via Wine on Linux
- Configurable UI — switch between detailed list and compact tile view, adjust icon size and font size
- Sort by name, version, release date, last update, disk usage, and more
- Disk usage displayed per installed app
- Custom app import — select a local folder containing a portable app and register it as a managed entry
- CLI argument support — pass arguments to any app at launch via an interactive parameter editor
- File and folder picker integration for building argument lists
- Automatic Linux path conversion for Wine (e.g. `/home/user/file.txt` becomes `Z:\home\user\file.txt`)

---

## Requirements

### Linux
- Any modern x64 Linux distribution
- [Wine](https://wine-hq.com/) or [Wine Staging](https://wine-staging.com/) installed and available on `PATH` (required to extract and run portable Windows apps)
- No .NET runtime required — ships as a self-contained single-file executable
- The `WINEPREFIX` environment variable is respected if set

### Windows
- Windows 10 or later (x64)
- No additional dependencies — ships as a self-contained single-file executable

---

## Building from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [NSIS](https://nsis.sourceforge.io/) (for building the Windows installer stub — `makensis` must be on `PATH`)

### Build

```bash
# Debug build (default)
./build.sh

# Release build
./build.sh Release
```

### Run directly (without full build)

```bash
cd src/Apportia
dotnet run
```