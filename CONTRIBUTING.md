# Contributing to Stowhaven

Thanks for helping improve Stowhaven.

## Before opening a change

- Create a feature branch for every change and open a pull request targeting `main`. Treat `main` as protected; do not commit or push changes directly to it.
- Use a GitHub issue for a substantial feature or behavior change so the design can be discussed first.
- Follow [SECURITY.md](SECURITY.md) for vulnerabilities; never disclose them in a public issue.
- Keep deployment-specific IDs and local filesystem paths out of tracked files.
- Do not add generated build output, credentials, token caches, recovery phrases, or real backup data.

After cloning, install the local branch guards:

```bash
sh scripts/install-git-hooks.sh
```

The pre-commit guard requires a feature branch; the pre-push guard checks destination refs and blocks pushes or deletions targeting `main`, including `HEAD:main`. Installed copies live in Git's common directory so they also work in linked worktrees and survive branch switches. Re-run the installer after hook updates. It refuses to replace existing custom hooks; integrate the versioned guards with those hooks instead. Local hooks complement GitHub's branch protection and must be installed in each clone.

## Development checks

Use the same verification command locally and in CI:

```bash
bash scripts/verify.sh
```

It restores dependencies, builds the Release solution (including Gateway), publishes the monitoring UI, and runs the .NET, Python packaging, and workflow automation tests. Full logs and publish output stay in a fresh OS temporary directory; failures print the log path and its last 60 lines. CI retains the full logs in collapsed job-log groups.

For infrastructure changes, include local Bicep compilation:

```bash
bash scripts/verify.sh --bicep
```

Standard verification needs .NET 10, Python 3, and Bash, plus Azure CLI/Bicep for `--bicep`. Docker is needed for the separate recovery and smoke checks described in [the testing guide](doc/TESTING.md).

Add or update tests for behavior changes. Documentation and example configuration should describe the current implementation and use placeholders for environment-specific values.

## Agent workflow

- Establish the feature branch before editing and preserve unrelated work, including untracked files.
- Restore dependencies before the first test baseline. During implementation, use focused test filters and narrow file reads; keep long issue/spec content and command output in temporary artifacts that can be referenced later.
- After review fixes, run the shared verification command. Repeat the full suite when subsequent changes or failures justify it.
- Finish with committed changes on the feature branch, a PR targeting `main`, and the PR's check status. Report a concrete blocker if publication cannot complete. Merge only when explicitly requested.

## Pull requests

Keep each pull request focused, explain user-visible and operational effects, and call out migrations or security tradeoffs. CI must pass before merge. Changes to authentication, storage retention, encryption, restore behavior, or infrastructure should include an explicit test and rollback plan.
