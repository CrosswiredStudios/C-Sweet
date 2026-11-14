# Agent CI dependency fallback

`scripts/prepare-ci-dependencies.py` is copied unchanged into each standalone agent
repository. Its `Resolver` prepares a job-local NuGet feed before any agent restore,
test, pack or publish. `scripts/test-ci-dependencies.py` is the matching offline
regression suite. No SDK or contracts source/project references are added to agents.

`agent_dependencies` reads literal `CSweet.Agent.SDK` and
`CSweet.WorkManagement.Contracts` references in `src/` and `tests/`. `Resolver.resolve`
downloads the exact package from NuGet's advertised package-content endpoint and
validates its nuspec identity/version. It recursively reads the SDK's contracts
dependency, including when the SDK is published but its contracts dependency is not.
Duplicate direct and transitive requirements are resolved once per package/version.

Only a package-content HTTP 404 activates fallback. Authentication failures,
rate-limit exhaustion, invalid packages, network failures and service outages fail
the job; they never silently select source, a newer package or another branch.

## Reviewed source pins

Every agent has `build/ci-dependencies.json`:

```json
{
  "schemaVersion": 1,
  "packages": {
    "CSweet.Agent.SDK": {
      "5.28.0": { "commit": "3f4d9ac410b53b82c880b4f50997d4c2e9626549" }
    },
    "CSweet.WorkManagement.Contracts": {
      "5.7.0": { "commit": "7e3e8be7eda16b0f48d33f4b3a0abdb33ebb3e76" }
    }
  }
}
```

`SOURCES` owns the repository identities: `CrosswiredStudios/CSweet.Agent.Sdk` and
`CrosswiredStudios/CSweet.WorkManagement.Contracts`. `Resolver.checkout` fetches only
the full reviewed commit, verifies HEAD, and checks the source project's `PackageId`
and `Version`. It never checks out a floating branch or substitutes a version.

When bumping an agent's SDK or contracts reference, update the matching commit pin
in the same change. Inspect the SDK project at that commit and pin its contracts
dependency too. A missing pin fails with an actionable message if the package is
unpublished. Multiple versions can be recorded when the dependency graph needs them.
An already published package remains the preferred source even with a source pin.

`Resolver.resolve` packs missing contracts before packing a missing SDK. Both use
package references with `UseLocalCSweetAgentSdk=false` and
`UseLocalCSweetWorkManagementContracts=false`; source versions are never overridden.
The packed nuspec must match the expected package version and source dependencies.
These packages stay in the job's temporary feed and are never pushed to NuGet.

## Workflow integration and evidence

Every .NET agent build job runs the regression suite and dependency preparation
after `actions/setup-dotnet`, before its first agent build. Preparation writes
`RestoreAdditionalProjectSources`, an isolated `NUGET_PACKAGES` location, and both
package-only switches to `GITHUB_ENV`. Additional sources work even in extension CI
jobs that explicitly set `RestoreSources` to NuGet.org. Existing test, self-test,
release-source validation and bundle checks remain in place.

Public dependency repositories need no secret. Private repositories can use the
optional `CSWEET_DEPENDENCY_READ_TOKEN` Actions secret with read-only repository
contents access. It is supplied only to Git fetch through process-local configuration;
it is not embedded in URLs, persisted in Git configuration or passed to .NET builds.
OpenHands' reusable workflow declares and forwards this optional secret.

`Resolver.prepare` writes `.artifacts/ci-dependencies/resolved.json`, containing
package versions, NuGet/source origins, source repository/commit where applicable,
and nupkg SHA-256 hashes. Each workflow uploads this file as a dependency-provenance
artifact and records the same origin/commit information in the GitHub job summary.
The workflow does not modify installation records or publish the temporary packages.

## Verification

Run the standalone regression suite from any updated agent root:

```powershell
python scripts/test-ci-dependencies.py
$env:CSWEET_RUN_DEPENDENCY_INTEGRATION = '1'
python scripts/test-ci-dependencies.py
```

The integration fixture commits two temporary local Git repositories, treats both
packages as unpublished, packs contracts followed by the SDK, and builds/runs a clean
package consumer with an explicit primary restore source. It requires Git and .NET 10
but makes no GitHub or NuGet requests. CI enables this fixture in every build job.

For an actual dependency preparation run, use `python scripts/prepare-ci-dependencies.py`.
Outside Actions, `--root` selects an agent and `--state` selects temporary build storage;
use the returned feed with `RestoreAdditionalProjectSources` and the two package-only
switches when invoking .NET locally. The script only exports environment settings
automatically when `GITHUB_ENV` is present.

`scripts/verify-agent-ci-dependencies.py` audits synchronized resolver/test copies,
workflow coverage, source pins and release metadata across the sibling agent roots.
Agent patch versions and release notes accompany this workflow change; the SDK and
contracts source repositories and package versions are unchanged.

## Bundled extension provenance

`scripts/verify-bundled-extension.py` validates the exact file bytes declared in
`extensions/video-game/extension.json`. Bundled `.cs`, `.md` and `.json` files use
`text eol=lf` attributes so Windows and Linux checkouts produce the same hashes.
After an intentional source edit, update its manifest digest from those LF bytes;
the verifier never normalizes content or regenerates hashes during CI.

`--self-test` stages the actual role-adapted extension with Windows CRLF input and
checks out three temporary trees using `core.autocrlf=true`, `false` and `input`.
Each tree must match every recorded SHA-256. Changed and missing source files must
fail validation. Both CI and release jobs run this check before dependency setup.
The original PowerShell checksum gate remains enabled in extension CI.

The November 14 provenance incident combined stale `VideoGameAgentKit.cs` hashes
with README hashes recorded from CRLF working copies. Compilation, tests and bundle
verification did not exercise the workflow's separate provenance gate. The fix
preserves the role-adapted source, refreshes its LF digests, adds explicit checkout
attributes and exercises this gate during local verification and release builds.
