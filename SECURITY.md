# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities privately through GitHub's private vulnerability reporting feature for this repository.

Include:

- The JellyTPM and Jellyfin versions
- A concise description of the issue and its impact
- Reproduction steps
- Sanitized Jellyfin log excerpts when relevant

Do not include API keys, authorization headers, private media paths, or sensitive media metadata in a public issue.

## Security boundaries

- JellyTPM routes require Jellyfin administrator elevation.
- Administrative API responses disable caching.
- Media paths are not returned to the browser.
- The current milestone is read-only and does not generate or delete trickplay data.

