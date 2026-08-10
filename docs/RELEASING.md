# Releasing Liftoff.Ipc

## Release model

The library project intentionally does not know how it is distributed. `Liftoff.Ipc/Liftoff.Ipc.csproj` contains build and dependency information only. The GitHub Actions workflow at `.github/workflows/publish-nuget.yml` owns all NuGet-specific behavior, including:

- package identity and descriptive metadata;
- package and assembly versioning;
- README and license inclusion;
- symbol package creation;
- package validation;
- NuGet.org authentication and destination.

Publishing a GitHub Release triggers the workflow. A tag such as `v1.2.3` becomes package version `1.2.3`; a tag such as `v1.2.3-beta.1` becomes `1.2.3-beta.1`. Tags must match the SemVer form enforced by the workflow. Build metadata such as `+build.4` is not accepted.

The workflow runs on Windows because the integration tests exercise Windows named pipes. It restores the solution, runs all unit tests and the integration tests on `net48`, `net8.0`, and `net10.0`, verifies mixed `net48`/`net10.0` communication in both directions, builds all three package assets, uploads the package as a workflow artifact, authenticates to NuGet.org with OIDC, and publishes the package and symbols.

## One-time infrastructure

The GitHub repository must have an environment named `nuget.org`. That environment must define `NUGET_USER` as the NuGet.org profile name, not an email address.

NuGet.org must have a Trusted Publishing policy with these values:

| Field | Value |
| --- | --- |
| Repository owner | `c-t-fallon` |
| Repository | `Liftoff.Ipc` |
| Workflow file | `publish-nuget.yml` |
| Environment | `nuget.org` |

The policy name is descriptive only; the current convention is `Liftoff.Ipc GitHub Releases`. No long-lived NuGet API key should be stored in GitHub.

## Creating a release

1. Choose a version that has never been published to NuGet.org.
2. Confirm the intended commit is present on `origin/master` and the working tree does not contain release changes that still need to be pushed.
3. Run the relevant tests locally when code has changed:

   ```powershell
   dotnet test IpcDemo.Tests.Unit/IpcDemo.Tests.Unit.csproj --configuration Release
   dotnet test IpcDemo.Tests.Integration/IpcDemo.Tests.Integration.csproj --framework net48 --configuration Release
   dotnet test IpcDemo.Tests.Integration/IpcDemo.Tests.Integration.csproj --framework net8.0 --configuration Release
   dotnet test IpcDemo.Tests.Integration/IpcDemo.Tests.Integration.csproj --framework net10.0 --configuration Release
   ```

4. Publish the release. For example:

   ```powershell
   gh release create v0.2.0 `
     --repo c-t-fallon/Liftoff.Ipc `
     --target master `
     --title "Liftoff.Ipc v0.2.0" `
     --generate-notes
   ```

5. Find and monitor the release-triggered workflow:

   ```powershell
   gh run list `
     --repo c-t-fallon/Liftoff.Ipc `
     --workflow publish-nuget.yml `
     --event release `
     --limit 3

   gh run watch <run-id> `
     --repo c-t-fallon/Liftoff.Ipc `
     --exit-status
   ```

6. Confirm that every workflow step passed, including `Publish to NuGet.org`. NuGet.org may take several minutes to index a newly accepted package.

## Failure recovery

First determine whether `Publish to NuGet.org` succeeded.

- If NuGet.org accepted the package, the version is immutable. Do not delete or move the tag to replace its contents. Fix any later problem and publish a new version.
- If the workflow failed before NuGet.org accepted the package, fix and push the workflow or code before retrying. A rerun retains the original event commit and workflow context, so recreating the release at the corrected commit may be necessary.
- Deleting a release or tag is destructive and requires explicit user authorization. Once authorized, a failed, unpublished version can be cleaned up with:

  ```powershell
  gh release delete v0.2.0 `
    --repo c-t-fallon/Liftoff.Ipc `
    --cleanup-tag `
    --yes
  ```

  Verify that the remote tag is gone, then recreate the release from the corrected `master` commit.

Do not bypass failing tests, replace Trusted Publishing with an API key, or move package metadata back into the project file merely to make a release pass. Diagnose the failing workflow step and preserve the release model.
