# Windows build artifacts

The **Windows build** GitHub Actions workflow builds a Windows x64 ZIP of C-Sweet
Headquarters. This is the first packaging milestone: unsigned application binaries
and browser assets. It is not yet an MSI or a configured, one-click installation.

## Create and download a build

1. Merge `.github/workflows/windows-build.yml` into the default branch.
2. Open **Actions > Windows build > Run workflow**, select a branch and optionally
   enter a version such as `0.1.0-preview.1`.
3. Download the `csweet-win-x64-...` artifact from the completed run. It contains
   `csweet-<version>-win-x64.zip` and a SHA-256 checksum file.

Pushing a `vMAJOR.MINOR.PATCH` tag (optionally with a prerelease suffix) also builds
that version. Manual runs without a version use `0.0.0-ci.<run number>`.
The workflow stores artifacts for 14 days; it does not publish a GitHub Release.
Pull requests changing the packaging workflow or scripts also validate the build.

The script can be run locally with PowerShell 7, Git, the SDK from `global.json`,
and network access:

```powershell
./scripts/release/Build-WindowsDistribution.ps1 -Version 0.1.0-preview.1
```

Output is under `artifacts/windows`. Each invocation uses fresh staging files;
an existing ZIP with the same version is never overwritten.

## Contents and runtime requirements

| Directory | Contents |
|---|---|
| `CSweet.Api` | Application API executable |
| `CSweet.AgentHost` | Agent policy and broker executable |
| `CSweet.WorkerHost` | Background work executable |
| `CSweet.ExecutionGateway` | Office connection gateway executable |
| `CSweet.Migrator` | Database migration executable |
| `CSweet.GitHost` | Source storage service executable |
| `CSweet.SourceControlProvisionerHost` | Source provisioning service executable |
| `web` | Published Blazor WebAssembly frontend |

The backend executables include the .NET runtime, so the target machine does not
need the .NET SDK, Git checkout, or sibling source repositories to load them.
Keep each executable with its accompanying files. This package excludes the
development Aspire AppHost, the MAUI client, and separately installed Office and
Compute providers.

Running the complete system still requires deployment configuration:

- PostgreSQL and a `ConnectionStrings__csweet` connection string for database consumers.
- Git on the source-storage service host for the GitHost and workspace snapshot operations.
- Run the Migrator successfully before starting database-dependent services.
- Configure internal service addresses, shared authentication keys, persistent data
  locations, gateway TLS certificates, and the public URLs appropriate to the host.
- Serve `web` through a static web server with SPA fallback and an API reverse proxy,
  or configure the frontend's `CSweet:ApiBaseUrl` and the corresponding API origin policy.
- Install and enroll Office separately to enable untrusted agent execution.

The authoritative development wiring is `src/CSweet.AppHost/Program.cs`; its
environment and service-reference configuration is not automatically applied to
these executables. A packaged launcher, configuration wizard, Windows service
registration, signing, and clean-machine startup tests remain subsequent work.

## Build dependencies and verification

`Build-WindowsDistribution.ps1` publishes explicit projects rather than the whole
solution, avoiding MAUI workloads and development orchestration. All optional
sibling project references are disabled. Published dependencies use the versions
in `Directory.Packages.props`.

Until Office.Contracts and its Isolation.Security dependency are available on NuGet,
`scripts/release/build-dependencies.json` pins their public repository commits.
The script checks out those exact commits, packs them into a temporary feed, checks
their package identities and versions, and consumes the packages. Update the pinned
Office.Contracts revision and version together with its central package pin. No
floating branch builds or developer-local dependency packages are used by CI.

The build fails on restore/publish errors or missing executable, runtime, or frontend
files. `build-info.json` records the application version, source commit, SDK and
dependency commits. The ZIP checksum detects download corruption; it is not a code
signature. Compilation and package-content checks do not establish successful
installation or end-to-end startup.
