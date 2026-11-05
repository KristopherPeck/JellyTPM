# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities privately through GitHub's private vulnerability reporting feature for this repository.

Include:

- The JellyTPM and Jellyfin versions
- A concise description of the issue and its impact
- Reproduction steps
- Sanitized Jellyfin log excerpts when relevant

Do not include API keys, authorization headers, private media paths, or sensitive media metadata in a public issue.

## Security boundary

- Every JellyTPM API route requires Jellyfin's elevated Administrator policy.
- Administrative API responses disable browser and intermediary caching.
- Plugin responses include item identifiers and display metadata but do not return full media paths.
- Browser-rendered metadata is inserted as text rather than executable HTML.
- Queue and history files use fixed names inside JellyTPM's plugin data directory.
- Generation is serialized through one worker, deduplicated by item, and bounded by a configurable active-queue limit.
- Queue-state changes are published in memory only after durable persistence succeeds. JellyTPM maintains a last-known-good backup and preserves malformed stores under quarantined file names for administrator inspection.

Jellyfin administrators are trusted users. Anyone with an administrator session can queue generation, clear terminal history, cancel queued jobs, change JellyTPM settings, and request regeneration.

## Destructive operations

Generate missing asks Jellyfin to preserve existing valid trickplay data. Regenerate requests replacement generation and may delete existing trickplay files and database records before new thumbnails are successfully created.

Regeneration requires explicit confirmation and is revalidated immediately before execution, but confirmation is an accident-prevention control rather than reauthentication. A compromised Jellyfin administrator session has the same authority as the administrator.

When Jellyfin is configured to save trickplay alongside media, generation and regeneration write within media storage. Maintain an appropriate Jellyfin backup and confirm storage permissions before using replacement operations. A failed regeneration can temporarily leave an item without trickplay data.

## Operational considerations

Trickplay generation invokes Jellyfin's native processing pipeline and can consume significant CPU, GPU, database, and storage resources. JellyTPM limits the active queue and runs one job at a time, but administrators remain responsible for choosing suitable batch and queue limits for their server.

Jobs interrupted by a server stop are returned to the queue. After three interrupted attempts, JellyTPM marks the job failed so a repeatedly disruptive item cannot restart automatically forever; an administrator can queue the item again after investigating the server log.

Inventory refreshes query every inventoried video and can place sustained load on Jellyfin's database for large libraries. Refreshes run as Jellyfin Scheduled Tasks and continue after the initiating administrator leaves the dashboard. New task configurations default to application-startup and daily 4:00 AM server-time triggers; administrators should choose triggers appropriate for their server or remove the startup trigger when startup load is undesirable.

The last successful inventory snapshot is stored in the JellyTPM plugin data directory and contains Jellyfin item identifiers and display metadata, but no media paths. Snapshot writes use a temporary file and atomic replacement. Oversized, malformed, or unsupported snapshots are not loaded and are quarantined for administrator inspection before a scheduled task rebuilds the cache.

Only install JellyTPM releases built for the server ABI stated in the manifest. Validate the release archive and its published checksums before distribution.

