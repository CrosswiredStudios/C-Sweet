# YouTube plugin preview verification

27 October 2026 Pacific. The plugin remains the current implementation focus; the conversational
YouTube agent is unchanged. Local verification supports a preview candidate, not production acceptance.

## Completed changes

`CSweet.Plugins.Platform.YouTube` is version `0.2.1`, using published SDK `5.5.0` and protocol `2.4`.
Its `connector-release.yml` workflow separates ordinary verification from protected publication.
The authoritative public consumer feed is nuget.org; the repository remains the reviewed plugin
installation source. `scripts/Test-Release.ps1` tests, self-tests, packs and inspects the package,
then compiles and runs a standalone consumer using an exact candidate version and fresh package cache.
Source mapping prevents an older public/global package from hiding a broken candidate. Release
artifacts contain the manifest, notes, package, SHA-256 checksums and source/verification evidence.
Publication setup and recovery instructions are in the connector repository's `RELEASING.md`.

`PluginManage.OpenConnect` starts a new account's provider flow directly after an explicit click.
The plugin's declared purpose, required access and policy links appear inline. The click binds
acknowledgement to the displayed package digest and policy version, while provider consent remains
on the provider's page. Optional scope sets are not included in first connection. Existing-account
reconnect/change-permissions retains the explicit review. Paused, unavailable or unassigned
connections cannot bypass readiness checks. Server-side authorization and OAuth checks are unchanged.

No YouTube-specific condition, OAuth credential, VM requirement or runtime scheduling control was
added to the host. The same native flow is tested with the independent example workspace provider.
Business sharing, consuming-agent grants and exact action approval remain separate.

## Verification evidence

- Connector: 139 tests passed, executable self-test passed, `0.2.1` NuGet package inspected, isolated
  NuGet-only consumer restored and ran. A separate empty-cache restore from nuget.org also passed.
- Host: 548 tests passed in the broader installation, connector, account, setup and runtime selection with
  packaged SDK `5.7.1` and WorkManagement contracts; local SDK/WorkManagement source references
  were disabled. Other pre-existing sibling references were not all disabled.
- Chromium: install review and required approval; direct base-access provider redirect with PKCE;
  paginated import; scoped operators; selected/whole-account business grants and future-resource
  acknowledgement; pause/resume; optional permission review; narrow-screen layout; disconnect
  impact; pending cleanup; removal after signed synthetic backup and native memory confirmations.
  The run completed without browser page errors. Desktop, mobile and cleanup screenshots were inspected.

The browser harness uses real native components and account services, but synthetic provider,
package-install and erasure-receipt data. It does not contact Google or exercise a deployed database,
real physical backups, the complete agent approval UI or multi-process PostgreSQL races.
Host dependencies still produce the previously recorded SignalR/cryptography/build-task security
warnings; this change does not resolve them or establish a green full-repository test suite.

## Remaining before confirming the plugin works with Google

1. Review and publish the connector candidate through the protected release path. Confirm the exact
   version is indexed and restorable from the authoritative feed. No package, tag or release was
   published by these local checks. The consuming agent's historical `0.2.0` pin remains unchanged.
2. Configure the intended deployment's customer-owned OAuth profile, exact HTTPS callback, APIs,
   test audience and policies using the Google checklist. The managed connection service now has a
   [separate foundation increment](managed-connection-service-progress.md), but host-managed account
   setup is not wired and this plugin release does not supply hosted credentials.
3. Review deployment migrations, cleanup adapters and security warnings. Install the exact approved
   connector commit/digest through the normal host flow, not by rewriting approved database records.
4. With an explicitly authorized channel owner, record real consent/partial consent, identity,
   actual channel discovery, refresh, business sharing and independent grants. Perform any provider
   mutation only with its explicit authorization and reconcile its durable result.
5. Verify real revocation and provenance-aware purge, including retained copies and physical storage
   receipts. Pending or unknown copies must remain unresolved. Complete provider verification and
   applicable eligibility/quota reviews before broader availability.

Only after this plugin acceptance should implementation move to the conversational agent.
