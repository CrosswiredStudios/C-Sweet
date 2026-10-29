# Dependency review — 2026-10-28

Primary Microsoft advisories:

- [SignalR CVE-2026-70159](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-334f-vvp4-7g6p): stateful reconnect backpressure can exhaust resources. Affected 10.0.0–10.0.12; fixed 10.0.13.
- [PKCS CVE-2026-84008](https://github.com/dotnet/runtime/security/advisories/GHSA-846f-f764-76mx): malicious certificate data can exhaust CPU in SignedCms.CheckSignature.
- [PKCS CVE-2026-70162](https://github.com/dotnet/runtime/security/advisories/GHSA-2fmv-rv2c-59x7): Windows EnvelopedCms.Decode processing can exhaust CPU. Both PKCS advisories affect through 10.0.12 and are fixed in 10.0.13.
- Signed XML denial-of-service limits are addressed by [CVE-2026-96958](https://github.com/dotnet/runtime/security/advisories/GHSA-r955-8q5j-3h83), [CVE-2026-96959](https://github.com/dotnet/runtime/security/advisories/GHSA-c399-9853-6jmj), and [CVE-2026-96960](https://github.com/dotnet/runtime/security/advisories/GHSA-7hqp-7c5m-4vq6). Affected through 10.0.12; fixed 10.0.13.

Source/resolved-graph assessment: production UI directly uses SignalR.Client and AppRealtimeState; API hosts AppEventsHub. No stateful-reconnect enablement was found, so the specific exploit configuration is not demonstrated. The vulnerable client package is nevertheless shipped and is patched. XML and PKCS 10.0.10 appeared in UnitTests/IntegrationTests resolved package libraries, not the inspected production API/Infrastructure/UI graphs. No direct SignedXml, SignedCms or EnvelopedCms calls were found under src; do not confuse RSA PKCS1 signatures or PKCS8 key import with these vulnerable CMS APIs. This bounds observed exposure; it is not a claim about every optional plugin or runtime deployment.

Scoped changes: existing SignalR.Client and Cryptography.Xml central pins move to 10.0.14. Logging.Abstractions must also move from 10.0.12 to 10.0.14 to satisfy the patched SignalR dependency graph; initial restore correctly rejected that downgrade. No unrelated package upgrades. Resolved UnitTests assets confirm XML and PKCS 10.0.14; UI assets confirm SignalR.Common and Logging.Abstractions 10.0.14. The refreshed affected-suite restore emitted no NU190 advisory warnings.

The staged service is self-contained with NETCore.App and AspNetCore.App 10.0.14 verified from its produced runtimeconfig/deps. This does not patch shared SDKs, existing containers, or deployed framework-dependent applications. Existing deployments still require their own servicing check before exposure.
