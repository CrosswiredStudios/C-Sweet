# GitHub Agent Import - Import and Sandbox Architecture

## Design principle

Imported agents are untrusted workloads. Containerization helps, but the real security boundary is
the combination of:

- Broker-mediated identity and capabilities.
- Platform-owned tool, MCP, API, and secret adapters.
- Disposable sandboxed execution.
- Explicit admin grants.
- Audit records for import, approval, execution, and revocation.

An agent may request access in its manifest. The application decides what it actually receives.

## Existing pieces to build on

Use the current broker and SDK model:

- `src/CSweet.Agent.Contracts/Packaging/AgentManifest.cs` defines the package manifest shape.
- `src/CSweet.Agent.SDK/AgentManifestLoader.cs` loads `csweet-agent.json`.
- `src/CSweet.AgentHost/Broker/ConfiguredAgentAuthorizationPolicy.cs` applies configured grants.
- `src/CSweet.AgentHost/Broker/AgentBrokerService.cs` accepts broker registrations.
- `src/CSweet.Agents.PersonalAssistant/csweet-agent.json` is the first-party example.

The root manifest should remain named `csweet-agent.json`.

## Repository layout

Minimum public repository shape:

```text
/
  csweet-agent.json
  README.md
  src/
```

Recommended future files:

```text
/
  csweet-agent.json
  csweet-lock.json
  SECURITY.md
  LICENSE
  README.md
```

The importer must require an explicit ref resolution. The system should resolve the default branch
to a commit SHA and store that SHA. Later updates are separate imports of a new immutable version.

## Import flow

1. User submits a public GitHub repository URL.
2. Import service normalizes the URL and fetches repository metadata.
3. Import service resolves the selected branch, tag, or commit to a commit SHA.
4. Import service reads only root `csweet-agent.json` first.
5. Manifest is parsed and validated without executing repository code.
6. System computes a manifest digest and records an import candidate.
7. UI shows publisher, agent ID, version, capabilities, publications, subscriptions, requested
   permissions, requested network access, runtime type, and warnings.
8. Admin approves, narrows, or rejects requested grants.
9. Package is materialized into an immutable local package cache keyed by source URL, commit SHA,
   manifest digest, and package digest.
10. Runtime launches the agent in a sandbox with broker endpoint and installation identity.

Do not run build scripts during manifest preview.

## Manifest policy

The manifest is a request. It is not an authorization source.

The persisted install grant must be the intersection of:

- Manifest requests.
- User/admin approval.
- Organization policy.
- Trust tier policy.
- Platform hard limits.

For community GitHub agents, default policy should deny:

- Raw secrets.
- Host filesystem mounts outside a disposable workspace.
- Docker socket access.
- Database access.
- Direct queue access.
- Unbounded network egress.
- Privileged containers.
- Host networking.
- Background services outside the agent lifecycle.

## Runtime types

Initial supported runtime for prebuilt community packages:

```json
{
  "runtime": {
    "type": "oci",
    "image": "ghcr.io/example/agent@sha256:..."
  }
}
```

This is safer than building arbitrary source locally. Source-only repositories can be supported
later by a controlled builder pipeline that produces a signed OCI image or `.csagent` package.

The existing `runtime.type = "executable"` can remain for first-party development agents, but
community imports should not use it by default.

For public GitHub repositories that contain a .NET agent project, use the companion Agent Runtime
Manager plan. It adds `runtime.type = "dotnet-project"` and requires the clone/build step to run in
an isolated builder container after admin approval.

## Sandbox profile

Run imported agents with a restrictive container profile:

- Read-only root filesystem.
- Writable disposable workspace mount only.
- Non-root user.
- Dropped Linux capabilities.
- No privileged mode.
- No Docker socket mount.
- No host PID, IPC, or network namespace.
- CPU, memory, process, and wall-clock limits.
- Log size limits.
- Egress through a controlled proxy or no network by default.
- Environment contains only broker address, installation identity, and short-lived workload token.

Container isolation is not enough by itself. It must be paired with broker and adapter controls.

## Broker relationship

Imported agents must communicate only with `CSweet.AgentHost`.

Agents should not receive:

- Database connection strings.
- RabbitMQ credentials.
- LLM provider keys.
- MCP server secrets.
- User API keys.
- Long-lived platform tokens.

The broker stamps:

- Agent ID.
- Installation ID.
- Business ID.
- Granted capabilities.
- Granted subscriptions.
- Granted publications.

The agent cannot self-assert a broader identity after registration.

## MCP and API access

Agents will need MCP servers and external APIs, but access should be brokered.

Preferred model:

```text
Imported agent
  -> broker capability request
CSweet platform adapter
  -> authorized MCP server or API
  -> redacted result
Broker
  -> agent
```

This lets the platform enforce:

- Per-agent tool allowlists.
- Per-business connection grants.
- Secret isolation.
- Data classification.
- Request/response logging.
- Rate limits.
- Approval gates for side effects.

An agent should not spawn arbitrary MCP servers from its own container unless the admin explicitly
installed and granted those servers. Even then, secrets should be supplied to the MCP runtime, not
to the agent process.

## Network policy

Default network posture for community imports:

- Broker endpoint: allowed.
- Package registry during runtime: denied.
- Public internet: denied unless requested and approved.
- Approved domains: allowlisted per install.
- Local network and metadata endpoints: denied.

Use `requestedNetworkAccess` as a declaration that drives admin review, not as a runtime rule by
itself.

## Secrets

Secrets should be referenced by opaque connection IDs. The agent can request a capability such as
`github.issue.create.v1` or `email.draft.create.v1`; the platform adapter retrieves the secret only
after checking policy.

No imported agent should receive raw API keys in normal task context or environment variables.

## Update and revocation model

Imported versions are immutable.

Changing any of these requires a new approval:

- Commit SHA.
- Manifest digest.
- Container image digest.
- Requested permission set.
- Requested network access.
- Runtime type.
- Publisher identity.

### Prebuilt release bundles (.csab)

Maintainers may attach a prebuilt `.csab` bundle (produced by `pack-csab.py`) to a tagged GitHub
release instead of requiring every install to compile from source:

- Preview calls `GET /repos/{owner}/{repo}/releases/latest` and selects the `*-linux-x64.csab`
  asset when the release is final (drafts and prereleases are ignored). The bundle is stamped
  onto the previewed row only when the release tag parses to exactly the manifest version at
  the previewed commit; a tag that is ahead of (or behind) the manifest — for example an
  uncommitted version bump at HEAD — is ignored so the row never advertises one version while
  carrying another version's payload. The stored row version is authoritative everywhere
  (update comparisons, install labeling); the tag is provenance for the download only.
  Without matching provenance the update falls back to a source build.
- Approval downloads the bundle (2 GiB cap), resolves the expected SHA-256 digest from the stored
  provenance or the `<bundle>.csab.sha256` sidecar, and imports it through the same
  `IAgentArtifactStore.ImportAsync` validation gate as builder-produced artifacts. A missing or
  mismatched checksum fails closed; the install never proceeds unverified.
- A failed prebuilt install falls back to the classic fleet source build (recorded as the next
  build attempt), so a bad release asset never blocks installation. Seal-phase failures
  (validation passed but the artifact store move/sign/persist failed) are reported on the
  install step with a "could not be sealed" message, and the update/retry endpoints return
  `AgentBuildException` text as a 400 `{ error }` body so the UI banner names the failure
  instead of showing a generic message.
- Provenance (`ReleaseTag`, `ReleaseAssetName`, `ReleaseAssetUrl`, `ReleaseBundleDigest`) is
  persisted on the package version, and the install popup reports download / verify / install
  steps with the release tag instead of the compile steps.
- Startup reconciliation (`IAgentBuildService.RecoverInterruptedAsync`, run by
  `AgentRuntimeStartupCleanupWorker` independently of workload cleanup) cancels jobs stranded
  in `Cloning`/`Building` by a restart and resumes them on the same path: prebuilt jobs
  re-run the bounded download/verify/install inline, source jobs are re-queued for the build
  worker, and jobs whose package already reached `Built` are cancelled without a new attempt.
  Manual retries (`RetryBuildAsync`) also prefer the prebuilt reinstall when the package
  carries release provenance.

Revocation should:

- Stop new assignments.
- Ask running containers to shut down, then terminate them.
- Revoke workload tokens.
- Preserve audit logs, task history, and artifacts.

## Data model additions

Suggested entities:

- `AgentPackageSource`: URL, host, repo owner, repo name, trust tier.
- `AgentPackageVersion`: source, commit SHA, manifest digest, package digest, manifest JSON,
  imported timestamp, status.
- `AgentInstallation`: business, package version, installation ID, enabled flag.
- `AgentGrant`: installation, capabilities, subscriptions, publications, permissions, network
  allowlist, approved by, approved at.
- `AgentRuntimeInstance`: installation, sandbox ID, status, started/stopped timestamps, exit code.
- `AgentAuditEvent`: import, approval, launch, stop, rejection, revocation, policy violation.

## Threats to handle explicitly

- Malicious manifest requests broad capabilities.
- Repository changes after approval.
- Image tag is replaced after approval.
- Agent tries to exfiltrate data through network egress.
- Agent asks an MCP server to perform unauthorized side effects.
- Agent attempts prompt injection through marketplace metadata or README content.
- Agent tries to overload broker queues or logs.
- Agent attempts lateral movement to Postgres, Docker, host files, or local network services.
- Agent publishes events outside its grant.
- Agent returns capability results for work it was not assigned.

Most of these are handled by making the broker and platform adapters authoritative.

## Rollout sequence

1. Bring Docker Compose up to parity with Aspire for `CSweet.AgentHost` and the Personal Assistant.
2. Add package/import persistence and root manifest preview.
3. Add admin grant review and deny-by-default install policy.
4. Add OCI-only sandbox runtime for imported community agents.
5. Route all agent tool/MCP/API usage through brokered platform adapters.
6. Add network allowlists, workload tokens, rate limits, and revocation.
7. Add signature/attestation support and trust badges.
8. Add source-build support only after the sandboxed OCI path is stable.
