# Changelog

All notable changes to JellyTPM will be documented in this file.

## [Unreleased]

### Added

- Initial Jellyfin 12.4 plugin scaffold.
- Read-only native trickplay inventory service.
- Server-wide and per-library coverage summaries.
- Paged and filtered per-item inventory API.
- Embedded administrator dashboard.
- Canonical eligibility and coverage status model.
- Guarded PowerShell release packaging with manifest checksum updates.
- Durable, single-worker trickplay generation queue with restart recovery.
- Per-item non-destructive Generate missing actions for Missing and Partial videos.
- Post-generation resolution validation, retained job history, and queued-job cancellation.
- Generation queue status cards, automatic polling, and recent job results in the dashboard.
- Server-side filtered batch generation with an independent configurable safety limit.
- Queue settings for terminal-history age, maximum retained records, and batch size.
- Clear-history controls that preserve queued and generating jobs.
- Four repository title-card concepts in the JellyTPM visual family.
- Canonical repository artwork and an exact 16:9 catalog image referenced by the plugin manifest.
- Accessible Coverage, Generation Queue, and Settings tabs for a less crowded dashboard.
- Persistent completed-job totals that can optionally follow retained history instead.
- Television series and season context captured in new generation queue records.
- Interactive trickplay previews with resolution selection and timestamp scrubbing.
- Guarded single-item replacement regeneration from the trickplay Preview dialog.
- Queue operation history distinguishing missing generation from regeneration.
- Configurable global active-queue limit enforced across single and batch requests.
- Compact active-job status records for inventory annotations.

### Fixed

- Added XML documentation for public inventory model properties so documentation-enabled builds complete without `CS1591` warnings.
- Normalized dashboard API responses across Jellyfin JSON casing modes and surfaced client-side request diagnostics.
- Added explicit server logging when inventory snapshot generation fails.
- Excluded synthetic Collections/BoxSets views that duplicate videos from source libraries, using Jellyfin's public `CollectionTypeOptions` model.
- Displayed disabled-library coverage as not applicable and exposed specific item eligibility labels.
- Removed music, book, and zero-video libraries from the trickplay dashboard and clarified when Partial coverage can occur.
- Added television series, season, owning-title, and extra-type context to item rows.
- Displayed each active item's actual Queued or Generating state while preserving its inventory coverage status.
- Matched combined show and season searches using Jellyfin's actual season names.
- Applied green, yellow, gray, and red queue-state styling and green 100% coverage bars.
- Renamed the Jellyfin sidebar entry from Trickplay Dashboard to JellyTPM.
- Replaced the inventory coverage sublabel with the active queue state to prevent status-cell resizing.
- Compacted the coverage filters and kept a manual inventory refresh in the same toolbar.
- Prevented stale or cached queue polls from leaving Queued and Generating counts or item states behind completed work.
- Avoided full-library inventory rebuilds when validating individual generation jobs.
- Coalesced concurrent forced inventory refreshes onto the newly built snapshot.
- Updated security documentation for generation, replacement, and media-storage risks.
- Reorganized the README around current coverage, preview, queue, and regeneration behavior.
- Normalized manifest arrays so post-update verification works across PowerShell versions.

