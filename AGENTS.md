# Repository instructions for agents

## Releases

Read `docs/RELEASING.md` completely before changing packaging or creating a release.

- Keep `Liftoff.Ipc.csproj` distribution-agnostic. Package identity, version, metadata, symbols, validation, credentials, and destination belong in `.github/workflows/publish-nuget.yml`.
- Do not add a NuGet API key. Publishing uses NuGet.org Trusted Publishing through GitHub OIDC.
- Do not publish a GitHub Release without explicit user authorization. Publishing a release immediately starts the NuGet deployment.
- Treat a version as immutable once NuGet.org accepts it. Never move or reuse its tag to change package contents.
- Run the release workflow on Windows because the integration suite exercises Windows named-pipe behavior.
