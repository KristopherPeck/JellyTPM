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

### Fixed

- Added XML documentation for public inventory model properties so documentation-enabled builds complete without `CS1591` warnings.
- Normalized dashboard API responses across Jellyfin JSON casing modes and surfaced client-side request diagnostics.
- Added explicit server logging when inventory snapshot generation fails.
- Excluded synthetic Collections/BoxSets views that duplicate videos from source libraries, using Jellyfin's public `CollectionTypeOptions` model.
- Displayed disabled-library coverage as not applicable and exposed specific item eligibility labels.
- Removed music, book, and zero-video libraries from the trickplay dashboard and clarified when Partial coverage can occur.
- Added television series, season, owning-title, and extra-type context to item rows.

