# JellyTPM

> Jellyfin TrickPlay Manager

[![Jellyfin 12.4](https://img.shields.io/badge/Jellyfin-12.4-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org/)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Status: beta](https://img.shields.io/badge/status-beta-orange)](https://github.com/KristopherPeck/JellyTPM/releases)
[![GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue)](LICENSE)

![JellyTPM trickplay coverage and generation dashboard](assets/title-cards/jellytpm-repository.png)

JellyTPM gives Jellyfin administrators a searchable view of native trickplay coverage and a controlled queue for generating, previewing, and replacing trickplay thumbnails.

> [!WARNING]
> JellyTPM is currently beta software.

## AI development disclosure

JellyTPM was designed and developed collaboratively by the project maintainer with assistance from OpenAI Codex. AI was used during research, implementation, refactoring, security review, troubleshooting, and documentation. AI-generated suggestions are treated as drafts rather than authoritative results; the maintainer directs the project, reviews changes, and is responsible for validating release candidates before publication.

## Features

- Server-wide and per-library coverage totals
- Server-side background inventory refreshes that continue after leaving the dashboard
- Configurable Jellyfin scheduled task for daily off-hours inventory reconciliation
- Paged inventory with library, status, title, show, and season filtering
- Available, Partial, Missing, Library disabled, and Not eligible states
- Expected widths, available widths, and thumbnail counts
- Television series, season, movie, and extra context where Jellyfin provides it
- Authenticated frame previews with resolution and timeline controls
- Non-destructive generation for individual Missing or Partial items
- Filtered batch generation with configurable batch and active-queue limits
- Explicitly confirmed replacement regeneration for one item
- Durable single-worker queue with restart recovery and per-item deduplication
- Post-generation validation, retained history, queued-job cancellation, and completed totals
- Separate Coverage, Generation Queue, and Settings tabs in Jellyfin's administrator dashboard

## Screenshots

### Coverage dashboard

Server-wide totals and per-library completion show where trickplay generation is still needed.

![JellyTPM Coverage dashboard showing server totals and per-library trickplay completion](assets/screenshots/coverage-dashboard.png)

### Generation queue

Queue status and retained history keep generation activity visible without leaving the Jellyfin dashboard.

![JellyTPM Generation Queue showing queued, generating, completed, and failed totals](assets/screenshots/generation-queue.png)

## How coverage is classified

| Status | Meaning |
| --- | --- |
| Available | Every currently configured effective trickplay width has thumbnails. |
| Partial | At least one configured width is available and at least one is missing. |
| Missing | No configured trickplay width is available. |
| Library disabled | Trickplay extraction is disabled for the containing library. |
| Not eligible | Jellyfin cannot process the video, or it is excluded by JellyTPM settings. |

An item is normally Partial only when multiple effective widths are configured. Effective widths account for the source video width.

Collections/BoxSets are excluded because they duplicate items from their source libraries. Music, book, and zero-video libraries are also omitted. Disabled video libraries remain visible with coverage shown as `N/A`.

## Generation and regeneration

Generate missing uses Jellyfin's non-replacement operation. JellyTPM runs one job at a time and marks it Completed only after every expected resolution has been verified.

Regenerate is available from the Preview dialog when an item already has trickplay data. It invokes Jellyfin's replacement operation, which can remove existing trickplay files and database records before rebuilding them. JellyTPM therefore requires explicit confirmation and prevents simultaneous jobs for the same item. If replacement fails, the item can temporarily have no usable trickplay preview.

Jellyfin does not expose reliable numeric per-file trickplay progress to plugins, so JellyTPM reports lifecycle states: Queued, Generating, Completed, Failed, or Cancelled.

Inventory refreshes run through Jellyfin's Scheduled Tasks system. Administrators can leave the dashboard while a scan is running and return later; the previous completed snapshot remains available until the replacement finishes successfully. The last successful snapshot is also restored as stale data after a server restart, so the dashboard does not need to remain empty while the startup task reconciles it. Opening the JellyTPM dashboard does not start a scan.

JellyTPM adds **Refresh JellyTPM inventory** under Jellyfin's Scheduled Tasks with default application-startup and daily 4:00 AM server-time triggers. Jellyfin applies these defaults only when no saved trigger configuration exists, so plugin updates do not overwrite an administrator's schedule. The dashboard's **Refresh inventory** button runs the same visible scheduled task. The task reports approximate progress after each 500-item library page, weighted by the previous snapshot's library sizes when available. Administrators can change or remove its triggers in Jellyfin. Verified JellyTPM generation results update the affected cached item and coverage counters immediately; scheduled and manual full scans reconcile changes made outside JellyTPM.

## Queue and history settings

| Setting | Default | Purpose |
| --- | ---: | --- |
| History retention | 30 days | Removes terminal records older than the configured age. |
| Maximum retained history | 50 | Limits detailed Completed, Failed, and Cancelled records. |
| Preserve completed total | On | Keeps the Completed counter when detailed history is pruned or cleared. |
| Maximum filtered batch size | 500 | Limits the candidates selected by one batch request. |
| Maximum active queue | 1,000 | Caps all Queued and Generating jobs across requests. |

Active jobs are never removed by history retention. Lowering the active-queue limit does not cancel existing work; it prevents new jobs until the queue falls below the limit.

## Requirements

- Jellyfin Server 12.4
- A Jellyfin administrator account
- .NET 10 SDK only when developing or packaging the plugin

JellyTPM should be installed only on the Jellyfin version targeted by its release manifest. Back up the Jellyfin data directory before testing beta releases or running broad replacement operations.

## Installation

### Plugin repository

1. Open **Dashboard → Plugins → Repositories**.
2. Select **Add**.
3. Name the repository `JellyTPM` and use this URL:

   ```text
   https://raw.githubusercontent.com/KristopherPeck/JellyTPM/main/manifest.json
   ```

4. Open **Catalog** and install JellyTPM from the **General** category.
5. Restart Jellyfin.
6. Open **Dashboard → JellyTPM**.

### Manual installation

The plugin repository method is preferred because Jellyfin creates and manages the plugin metadata. For a manual installation or upgrade:

1. Download `JellyTPM_<version>.zip` from the matching [GitHub release](https://github.com/KristopherPeck/JellyTPM/releases).
2. Stop Jellyfin before changing plugin files.
3. At the top level of Jellyfin's plugin directory, move aside or remove old binary directories named `JellyTPM` or `JellyTPM_<old-version>`. Do not remove the `configurations` directory or JellyTPM data files.
4. Create a versioned directory named `JellyTPM_<version>` using the exact four-part release version, such as `JellyTPM_0.7.0.0`.
5. Extract the release archive directly into that versioned directory.
6. Start Jellyfin and confirm the installed version on the Plugins page.

> [!IMPORTANT]
> Do not manually install JellyTPM into an unversioned `JellyTPM` directory. Without catalog metadata, Jellyfin assigns an unversioned plugin directory the server's version, which can cause it to supersede catalog-installed JellyTPM releases or display inconsistent installed versions.

Common plugin locations include:

| Installation | Plugin directory |
| --- | --- |
| Windows tray install | `%ProgramData%\Jellyfin\Server\plugins` |
| Windows user install | `%LocalAppData%\jellyfin\plugins` |
| Common Linux packages | `/var/lib/jellyfin/plugins` |

## Development

JellyTPM is a conventional .NET Jellyfin server plugin. `Web/dashboard.html` is embedded directly in the plugin assembly; Node.js and Docker are not used.

The project references `Jellyfin.Controller` and `Jellyfin.Model` 12.4.0.

Testing and packaging are intentionally performed on a separate designated workstation.

### Release packaging

From PowerShell on the build workstation:

```powershell
.\scripts\package-release.ps1
```

The script restores and publishes the project version, stages an allowlist of plugin files, rejects server assemblies and native runtime files, and writes the ZIP plus MD5 and SHA-256 checksum files beneath `artifacts/`.

After the exact release ZIP has been validated, update its matching manifest checksum and UTC timestamp with a fresh build:

```powershell
.\scripts\package-release.ps1 -UpdateManifest
```

`-SkipBuild` can repackage an existing `artifacts/publish/<version>` directory, but it cannot be combined with `-UpdateManifest`.

## Security

All JellyTPM API routes require Jellyfin's elevated Administrator policy. Administrative responses disable caching, and plugin responses do not expose full media paths.

Generation can consume substantial CPU, GPU, and storage I/O. Regeneration is destructive to existing trickplay data by design, and administrators should use it only after confirming the selected item and maintaining an appropriate Jellyfin backup.

See [SECURITY.md](SECURITY.md) for the security boundary and private vulnerability-reporting guidance.

## Roadmap

- Retry controls for failed jobs
- Best-effort cancellation of active generation when Jellyfin exposes a safe mechanism
- Detection of native Jellyfin scheduled-task generation activity

## License

JellyTPM is licensed under [GPL-3.0-only](LICENSE).

