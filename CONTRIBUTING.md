# Contributing to Stowhaven

Thanks for helping improve Stowhaven.

## Before opening a change

- Use a GitHub issue for a substantial feature or behavior change so the design can be discussed first.
- Follow [SECURITY.md](SECURITY.md) for vulnerabilities; never disclose them in a public issue.
- Keep deployment-specific IDs and local filesystem paths out of tracked files.
- Do not add generated build output, credentials, token caches, recovery phrases, or real backup data.

## Development checks

Start the local dependencies with `docker compose up --build`. Before opening a pull request, run:

```bash
dotnet restore FlorisDeV.BackupApi.sln
dotnet build FlorisDeV.BackupApi.sln --configuration Release --no-restore
dotnet build src/services/gateway/Gateway.csproj --configuration Release
dotnet test FlorisDeV.BackupApi.sln --configuration Release --no-build
az bicep build --file deploy/bicep/main.bicep
```

Add or update tests for behavior changes. Documentation and example configuration should describe the current implementation and use placeholders for environment-specific values.

## Pull requests

Keep each pull request focused, explain user-visible and operational effects, and call out migrations or security tradeoffs. CI must pass before merge. Changes to authentication, storage retention, encryption, restore behavior, or infrastructure should include an explicit test and rollback plan.
