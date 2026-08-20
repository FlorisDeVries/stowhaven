# Security policy

## Supported version

Security fixes are applied to the latest revision on `main`. Older commits, deployment snapshots, and forks are not maintained by this repository.

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability or include credentials, tokens, SAS URLs, personal file paths, or recovery phrases in an issue.

Use GitHub's private vulnerability reporting flow from the repository's **Security** tab. Include:

- the affected component and revision;
- reproduction steps or a minimal proof of concept;
- the expected and observed security boundary;
- the likely impact; and
- any suggested mitigation.

If private vulnerability reporting is unavailable, contact the maintainer privately through the contact method on their GitHub profile and share only enough detail to establish a secure follow-up channel.

This is a self-hosted backup system. Operators are responsible for protecting their Azure tenant, GitHub Actions secrets, GHCR tokens, client token caches, encryption recovery phrases, and restore destinations. Never use production credentials in a bug reproduction.
