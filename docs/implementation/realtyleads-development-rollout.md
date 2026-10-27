# RealtyLeadsAI isolated development rollout plan

Status: plan only. No environment, credential, account grant, published package or
production deployment has been created by this plan. Existing synthetic acceptance
uses `tests/CSweet.RemoteMcp.AcceptanceHarness` on loopback port 5287 with ephemeral
storage; never enter a real key into that fake-authentication harness.

## Smallest useful stages

1. Repeat synthetic acceptance with the existing harness. It needs no database
   server, external MCP, compute node, real identity or new access grant. This is
   already verified and is the smallest local demonstration.
2. For real native account storage and provider reads, use the actual API,
   browser application and AgentHost against a new empty development PostgreSQL
   cluster. Use real C-Sweet local authentication and antiforgery, not the fixture
   identity. Register only the two reviewed packages and a new test business.
3. Actual deployed agent execution additionally needs a separately enrolled,
   isolated supported compute node/image. The Mac test runtime exercises the actual
   agent callbacks but is not proof of a deployed workload. Do not enroll an existing
   production node or reuse its certificates. If a suitable disposable node is not
   available, stop at native storage verification and report this specific gap.

## Proposed addresses and storage

Use an unused directory beneath this task workspace named `realty-dev`, with mode
0700. Check port availability before starting; do not stop any existing listener.
All services bind loopback only; no tunnel, reverse proxy, public DNS or container
changes are needed.

| Component | Local binding | Isolated storage |
| --- | --- | --- |
| PostgreSQL | 127.0.0.1:17432 | realty-dev/postgres; new database csweet_realty_dev |
| C-Sweet API | https://localhost:17443 | new database; realty-dev/api-logs |
| AgentHost gateway | https://localhost:17444 | same new database; realty-dev/agenthost-logs |
| Browser app | https://localhost:17445 | realty-dev/web; fresh browser profile |
| Artifact store | no listener | realty-dev/artifacts |
| Source archives / snapshots / build logs | no listener | realty-dev/source-archives, snapshots, build-logs |

PostgreSQL `postgres`/`initdb` were not found on this Mac's PATH during review.
Provisioning a new local PostgreSQL executable/process (or a new explicitly approved
container) is a prerequisite; do not connect to an existing database to work around it.
Use a development TLS certificate scoped to localhost; installing/trusting a new
certificate also needs explicit authorization if none is already suitable.

Set `ConnectionStrings:Postgres` only in private development configuration, pointing
to 127.0.0.1:17432 and the new database. Do not inherit user secrets or existing
service configuration. Use a new database role with access to that database only.
Data Protection keys persist in the development database under the existing host
configuration; secure its filesystem/backups and never copy production key-ring rows.

Set `CSweet:AgentRuntime:Artifacts:Provider=filesystem` and `RootPath` to the isolated
artifact directory. Set `CSweet:AgentRuntime:SourceArchiveStorePath`,
`WorkspaceSnapshotStorePath` and `BuildLogStorePath` to the directories above.
Point the copied browser output's `CSweet:ApiBaseUrl` at https://localhost:17443/.
Run with Development environment for the existing loopback CORS policy, never with
Testing environment (which bypasses API antiforgery). Start only needed processes,
not the complete AppHost orchestration. Run migrations only against the empty
verified development connection string. Do not copy production business data,
accounts, plugin records, grants, catalog installations, node registrations or keys.

## Package and identity prerequisites

- SDK 5.6.0 is packed locally. Configure only the development build/package feed
  to consume it; publishing it publicly is a separate approval.
- The reviewed provider currently supports immutable repository imports only.
  Source archives and executable bundle uploads are rejected. The reviewed plugin
  and agent feature commits therefore need to be made accessible through an
  approved repository/ref before normal import/build/install. Pushing those commits
  requires explicit authorization; no package records or manifests may be patched
  directly in the database as a shortcut.
- Use the normal native import review and install flows with exact source identity
  and package digest. The provider must verify with no VM/runtime or schedule.
- Create a fresh local human user and test business. Owner must be an active human
  Manager or above. Use no SMTP, LLM, cloud-storage or production service credentials.
- Installing the consumer and executing it requires the normal approved capability
  grants, workload identity and isolated compute enrollment. Creating these is a
  separate authorized development action, not implicit in this plan.

## Human-controlled acceptance after authorization

The human enters their authorized subscription-scoped RealtyLeadsAI key directly
in the native account page. Do not put it in chat, terminal commands, environment,
files or agent arguments. Connection creation grants nothing and does not verify
provider company identity. Select the one intended agent and approve read access.

Run one search with limit 1, then look up the returned ID. Check that only reviewed
lead fields reach the agent. Revoke in native settings; confirm further reads are
denied, sessions/queued work are fenced and the stored key is removed. If testing
rotation, use another human-entered key and require fresh consent. Do not deliberately
send another user's key or query unowned provider company IDs.

On completion stop only the development processes, then remove the disposable
business/data after inspection and revoke the test provider key on RealtyLeadsAI.
Retention or deletion of the development database is a human decision. No production
rollout, push, publication, new provider credential, compute enrollment or native
account grant is authorized by this document.
