# JellyTPM

> Jellyfin Trickplay Manager

JellyTPM gives Jellyfin administrators a per-library and per-item view of native trickplay coverage, with a durable queue for generating missing thumbnails and tracking verified results.

## Current status

JellyTPM is early development software targeting Jellyfin Server 12.4. Phase 2 adds controlled, non-destructive per-item generation to the inventory dashboard.

Implemented:

- Server-wide native trickplay coverage totals
- Per-library completion percentages
- Available, partial, missing, disabled, and ineligible item states
- Server-paged title inventory with library, status, and search filters
- Expected and available resolution reporting
- Short-lived server-side inventory snapshots
- Administrator-only API routes
- Embedded Jellyfin dashboard without Node.js or a frontend build toolchain
- Durable single-worker generation queue that recovers interrupted work after restart
- Non-destructive Generate missing actions for Missing and Partial items
- Post-generation resolution validation, retained job history, and queued-job cancellation

Planned:

- Generate missing by library
- Explicit replacement regeneration
- Retry failed jobs and request cancellation of active work
- Native scheduled-task activity detection

## Status definitions

| Status | Meaning |
| --- | --- |
| Available | Every currently configured trickplay width is recorded. |
| Partial | At least one configured width exists and at least one is missing. |
| Missing | No configured trickplay width is available. |
| Library disabled | Trickplay extraction is disabled for the containing library. |
| Not eligible | Jellyfin cannot process the video, or it is excluded by JellyTPM settings. |

JellyTPM does not report a numeric percentage for the active FFmpeg operation because Jellyfin does not expose reliable per-file trickplay progress through its plugin API.

Generate missing uses Jellyfin's non-replacement operation. A job is marked Completed only after JellyTPM confirms that every expected resolution exists. Queue and history records are stored in the plugin data directory and completed records are retained for 30 days by default.

Synthetic Collections/BoxSets views are excluded because they reference videos from physical source libraries and would otherwise duplicate inventory rows. Music, book, and zero-video libraries are omitted because they are outside the scope of native video trickplay. Disabled video libraries show coverage as `N/A` rather than implying that generation failed.

Episode rows include their television series and season. Extra rows include their owning movie or episode context when Jellyfin exposes that ownership relationship.

An item can normally be Partial only when multiple effective trickplay widths are configured and some, but not all, are available.

## Requirements

- Jellyfin Server 12.4
- .NET 10 for development and packaging
- A Jellyfin administrator account to access the dashboard

## Development

JellyTPM is a conventional .NET Jellyfin server plugin. It does not require Node.js or Docker.

The project references Jellyfin's 12.4 controller and model packages and embeds `Web/dashboard.html` directly in the plugin assembly.

Testing and packaging are intentionally performed on a separate designated workstation.

### Release packaging

From PowerShell on the build workstation:

```powershell
.\scripts\package-release.ps1
```

The script restores and publishes the version declared in the project, stages only the plugin runtime files and notices, creates MD5 and SHA-256 checksum files, and writes the ZIP beneath `artifacts/`.

For a final release candidate whose ZIP has been validated, update the matching manifest checksum and UTC timestamp during packaging:

```powershell
.\scripts\package-release.ps1 -UpdateManifest
```

Use `-SkipBuild` only to repackage an existing `artifacts/publish/<version>` directory. It cannot be combined with `-UpdateManifest`.

## Security

All JellyTPM API routes require Jellyfin's elevated-privilege authorization policy. Browser responses contain item identifiers and display metadata but never full media paths.

See [SECURITY.md](SECURITY.md) for vulnerability reporting guidance.

## License

JellyTPM is licensed under GPL-3.0-only. See [LICENSE](LICENSE).

