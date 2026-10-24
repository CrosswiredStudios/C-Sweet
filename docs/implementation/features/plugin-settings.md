# Plugin settings and bundles

`CSweet.UI.Pages.Plugins` at `/settings/plugins` has three tabs:

- **Installed** lists only service and connector installations. Search, version, scope,
  enable/disable, runtime/build diagnostics, removal confirmation, and declared OAuth provider
  administration are available here. Agent packages remain in Installed Agents.
- **Find plugins** searches the bundled starter catalog in `PluginCatalog.Entries` by name,
  description, or category. Installed entries open their management section. Review reads the
  current repository manifest; a catalog entry grants no trust or permissions.
- **Install** accepts a public GitHub plugin repository URL (with an optional branch, tag,
  or commit) or a prebuilt `.cspb` upload. Repository preview resolves and pins an immutable
  commit. Review shows the publisher, version, requested capabilities, subscriptions, network
  access, configuration, warnings, and package digests. Connectors require a selected business;
  services use the existing system-wide, always-on policy. All-public web access has a separate
  acknowledgement. Installation uses the existing approved-grant flow.

`PluginManagementEndpoints` adds `POST /api/plugins/imports/preview/bundle?fileName=...`.
Its body is the raw bundle stream, bounded to 512 MiB by both the endpoint and
`PluginBundleImportService`. It retains plugin-administration authorization, import rate limits,
and the API's antiforgery middleware. Invalid files return a normal preview error.

## `.cspb` format

A `.cspb` is an uncompressed PAX TAR using the same artifact format 1.0 as prebuilt agent
bundles. It contains `artifact.json`, the self-contained Linux application under `payload/`,
and the exact `payload/csweet-plugin.json` manifest. Only `service` and `connector` kinds are
accepted. Supported architectures are `x64` and `arm64`. The executable named by
`artifact.json.entrypoint` must exist and carry executable permission bits. Paths, links,
ownership, special files, duplicate/case-colliding names, modes, and size limits pass through
the shared `IAgentArtifactStore.ImportAsync` gate.

Create one after publishing the plugin (example):

```powershell
dotnet publish src/MyPlugin/MyPlugin.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
python scripts/pack-cspb.py --manifest csweet-plugin.json --publish-dir publish/linux-x64 --out publish/MyPlugin-1.0.0-linux-x64.cspb
```

Run the packer from the C-Sweet checkout or use its absolute path. Manifest and publish paths
refer to the plugin repository. The packer includes declared workstream/toolchain definition
resources when they are absent from publish output, normalizes TAR metadata, and prints SHA-256.
An existing output file is never overwritten. `.csab` remains the agent package extension;
ZIP source archives are a separate legacy endpoint and are not accepted in this upload control.

`PluginBundleImportService.PreviewAsync` stages the bounded stream in the configured source
archive store, verifies its exact SHA-256 and full artifact structure, validates the embedded
manifest and contributed definitions, and seals the immutable artifact. The staging file is
removed on success or failure. Identical bytes reuse one package even if the filename changes.
The package status `Built` records artifact readiness; preview creates no installation, grant,
runtime, or source-build job. `AgentInstallationService.InstallCoreAsync` creates the approved
installation and grants only after normal scope, configuration, and permission validation.
Uploaded bundles therefore skip the source build. `AgentImportPreviewService.ToResponse`
preserves connector provider/setup metadata for both repository and bundle previews.

Verification: `PluginBundleImportServiceTests`, `PluginProviderAdministrationTests`,
`SettingsWorkspaceRenderingTests`, `AgentImportPreviewServiceTests`, and
`AgentInstallationServiceTests` cover the package gate, authority boundary, and settings flow.
