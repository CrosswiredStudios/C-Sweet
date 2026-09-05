# Internal Git hosting

C-Sweet now has an internal repository store behind the existing authenticated GitHost boundary. The implementation is incremental; the complete offline developer workflow is not yet delivered.

## Available

- Business source-control page: create, inspect, rename, change the default branch, archive, restore, and delete unused archived repositories. Browse refs, recent history and files; create and delete branches/tags. Destructive controls require confirmation.
- Business managers/owners administer repositories; active members inspect them. Core resolves business ownership before calling GitHost. Started/completed management operations enter the existing append-only audit ledger.
- New businesses receive an internal Git connection and an empty-repository creation policy without creating a repository. Existing businesses obtain the connection when creating their first internal repository. GitHub configuration is optional and no longer gates the business page.
- Native bare Git storage, validated refs, exact-old-SHA updates, bounded Git processes, disabled external protocol access and repository-supplied hooks, and process-independent repository locks.
- Brokered internal workspace preparation, inspection, publication, refresh and cleanup. Core resolves persisted repository/assignment authority; operations recheck active employee/team membership and repository policy. Guests receive sanitized snapshots and metadata, not Git credentials or host paths.
- Publication writes native Git commits to the assigned work branch with atomic expected-old-SHA updates and durable receipt refs. Retries reuse the same commit and reject altered content. Main/default-branch writes are rejected by publication. Changed files are persisted with the proposed change.
- Preparation initializes an empty repository on first use, resolves an exact commit, preserves tracked files despite release-archive attributes, and materializes LFS assets. Publication converts LFS-tracked assets back into verified pointers without executing repository filters. Gitlinks are retained; submodule content is not materialized.
- Refresh replaces clean snapshots at the latest work-branch revision (or default branch before publication), rejects conflicting local edits, and recognizes a completed import after a lost response. Cleanup retains dirty work when requested and tolerates a lost response after removal.
- Internal proposed changes use the existing exact-SHA QA, signed lead authorization and optional administrator approval workflow. GitHost merges with expected-head verification, conflict rejection and atomic merge receipts. Replayed merges cannot switch source/target identity. Publication status changes and supersession are persisted.
- Business page: assign, inspect, edit and revoke team repository access; choose primary repositories and merge approval policy with revision checks. Inspect proposed-change status, exact-SHA QA and diffs, including the original review diff after merging. Publication links open the repository/work branch directly.
- LFS object storage supports filesystem and S3-compatible endpoints (including MinIO), repository-scoped keys, atomic filesystem publication and SHA-256/size validation. Authenticated Git LFS batch/basic upload and download APIs are exposed; LFS locking is not yet implemented.
- Settings source-control page displays effective storage locations, repository readiness, and optional GitHub configuration. Repository readiness probes file creation, flush, rename, and the Git executable; it is not an S3 connectivity or backup check.
- Aspire forwards storage configuration to GitHost. Docker Compose includes GitHost, persistent data and a private shared-key volume. The running local app must be restarted to load changes.

## Git client access

Open an internal repository on the business source-control page and expand **Git client access**. Create a named token, save it immediately, and use username `csweet` with that token as the password in the client's credential manager. The page provides the clone URL. Tokens expire after 1–90 days, are shown once, and only their SHA-256 hashes are stored. Active human members can create read-only tokens; managers can create push tokens. Members list/revoke their own credentials, and managers can inspect/revoke all credentials for that repository. Every Git and LFS request rechecks the credential's repository scope, expiration, revocation and current human membership; push also rechecks current manager permission and active repository status.

Clone, fetch, pull, branch/tag pushes and Git LFS batch/basic transfers use standard HTTP endpoints on Core at `/git/{business}/{repository}.git`. GitHost remains private behind signed service authentication. A host-owned pre-receive hook protects active agent branches and disallows unsupported ref namespaces. Default-branch writes require an explicit additional option when creating a manager push token; this is intentional direct human administration, separate from the governed agent merge workflow. Non-fast-forward branch updates are rejected. Internal receipt refs are hidden and cannot be overwritten by Git clients. Credential lifecycle and push transfer attempts are audited; a completed transfer audit event does not by itself imply every ref update was accepted.

Human Git/LFS requires HTTPS, with an exception for direct loopback development connections. Set `CSweet:SourceControl:PublicGitBaseUrl` on Core when its externally accessible origin differs from the request origin. TLS reverse proxies must be explicitly trusted via `CSweet:Http:KnownProxies` (an array of proxy IP addresses); forwarded HTTPS headers from arbitrary clients are not trusted. The bundled nginx configuration forwards `/git/` and preserves the public port. A production reverse proxy must terminate TLS and forward the correct protocol through the configured trusted proxy path; the stock plain-HTTP Docker endpoint alone does not enable remote credential transport.

Current buffered client limits are 128 MiB per Git request or LFS object, and 256 MiB per Git response. The backing LFS storage's larger configured object limit does not override this transport limit. Larger streaming transfers, resumable object upload, range downloads and LFS locking remain future work. Native Git and Git LFS tests run against an isolated localhost HTTP server; physical NAS/MinIO and production TLS proxy deployments remain unverified.

The transport follows the [Git HTTP protocol](https://git-scm.com/docs/gitprotocol-http) and [Git LFS batch/basic transfer API](https://github.com/git-lfs/git-lfs/tree/main/docs/api).

## Agent repository creation

The existing `source-control.repository.provision.v2` capability now accepts the built-in internal empty repository when `TemplateId` is omitted/`Guid.Empty`. An explicit approved template ID continues to select its connection, including Managed GitHub. No external SDK package change is required for this existing request shape. The agent must hold both the runtime capability and the scoped organization grant; package identity no longer limits creation to two named agents.

New businesses default to enabled internal creation, a quota of 100 active managed repositories, and no per-repository manager approval. These defaults do not grant the capability to agents. The business page's **Agent repository creation** panel manages enablement, quota, optional manager approval, prefix, initial branch, default team and recent request status. Existing businesses initialize this policy when the manager opens these controls or an authorized agent first requests an internal repository. An explicit opt-out is preserved.

An agent with exactly one active team uses that team when the policy has no default. With multiple teams, a manager selects the default; the requester must belong to it. Creation attaches the repository to that team and makes it primary only if the team has no active primary repository. Pending and approval requests reserve quota. Connection concurrency tokens serialize concurrent agent quota reservations. The worker rechecks current scoped grants, enabled installation, active team membership and policy revision before creating storage.

Internal creation persists an immutable repository ID before contacting GitHost. A storage/transport failure leaves a recoverable provisioning request; after two minutes the worker resumes with the same ID. Revoked authority and changed policy fail the request instead of creating storage. Changing a policy deliberately invalidates queued requests authorized at the previous revision. Existing Managed GitHub provisioning retains its current template and approval behavior.

## Storage configuration

Configure GitHost through `CSweet:SourceControl:Storage` (environment variables use double underscores). Configuration is deployment-owned; editing a path does not relocate data.

```json
{
  "CSweet": {
    "SourceControl": {
      "Storage": {
        "RepositoryRoot": "/mnt/company-nas/csweet/repositories",
        "ExpectedStoreId": "company-source-control",
        "TemporaryRoot": "/var/tmp/csweet-git",
        "Lfs": {
          "Provider": "s3",
          "ServiceUrl": "https://minio.company.internal:9000",
          "BucketName": "csweet-lfs",
          "KeyPrefix": "source-control",
          "Region": "us-east-1",
          "ForcePathStyle": true
        },
        "Backup": {
          "Provider": "filesystem",
          "RootPath": "/mnt/company-nas/csweet/backups"
        }
      }
    }
  }
}
```

The default repository root is the current service user's local application-data directory under `CSweet/SourceControl/repositories`. Temporary work defaults to the system temporary directory under `csweet-git`. LFS and backup filesystem locations default to sibling directories of the repository root. Backup settings reserve the configuration shape; backup execution is outstanding.

A custom repository root must already contain `.csweet-git-store`, whose text equals `ExpectedStoreId`. Provision this marker on the intended mounted storage using the service account. Missing/mismatched markers fail closed without initializing replacement local storage. Windows UNC paths and mounted SMB/NFS paths are accepted. NAS storage must support exclusive file access, atomic rename and durable writes; verify the actual appliance configuration before use. Run only one active GitHost per repository store.

A separately configured filesystem LFS `RootPath` requires its own `.csweet-object-store` marker and `Lfs:ExpectedStoreId`. S3 uses `AccessKeyId`/`SecretAccessKey` from operator-provided configuration or the AWS SDK's credential chain. Do not commit credentials. Use HTTPS except for loopback endpoints. Buckets must be provisioned and private.

In Docker Compose, `CSWEET_GIT_DATA_VOLUME` defaults to the named volume `csweet-git-data`. It can instead be an absolute NAS mount path, mapped to `/data`. The default named volume receives the image's `repositories/.csweet-git-store` marker. For a NAS mount, provision `/data/repositories/.csweet-git-store` on that share and set `CSWEET_GIT_STORE_ID` to its contents. Mount credentials belong to the host, not to C-Sweet repository records.

GitHost creates its private service key once in `/trust/git.key`; consumers receive this volume read-only. Aspire continues to use its existing generated trusted-service key. Neither mechanism involves an external hosting account.

For relocation: stop writers, copy repositories and LFS objects with metadata, verify Git integrity and LFS hashes, configure the new locations and matching identities, restart, verify reads/writes, then retain the original until the operator explicitly retires it. Changing configuration alone does not migrate data.

## Remaining implementation

- Provider-neutral routing for GitHub workspace operations beyond preparation. The newly wired operation path currently accepts internal repositories only; GitHub operations that were previously unavailable remain unavailable.
- Extend provider-neutral discovery and the SDK surface beyond the current existing request shape, and remove remaining fixed branch/workflow assumptions. Repository provisioning now uses grants instead of named-agent allowlists and supports an empty internal repository.
- Business default-provider selection, connection CRUD, broader scoped grants and integrated audit browsing. Team assignment/revocation and existing merge-policy controls are implemented.
- Larger streaming Git/LFS transfers, resumable object uploads, LFS file locking, and production TLS proxy verification. Scoped credentials, human Git clone/pull/push and LFS batch/basic transfers are implemented.
- Backup/restore execution and management; resumable storage relocation rather than the current operator procedure.
- Personal GitHub repository creation and explicit, resumable migration of Git history/LFS with clean handoff and verified cutover.
- Offline end-to-end agent delivery, actual NAS/MinIO integration tests, rendered browser verification, and full deployment testing.

## Verification

Focused tests cover native Git publication and merge receipts, stale branch rejection, merge conflicts, LFS round-tripping, complete snapshot preparation, dirty refresh/cleanup retries, revoked assignments/team access, repository management authorization, team policy revisions, proposal isolation, exact-SHA governance, native HTTP Git clone/pull/push, protected refs, credential scoping/revocation/expiry, native LFS client round-trips, on-demand agent provisioning, quota reservations, revoked provisioning grants, recovery identity, and manager policy updates. Application builds use isolated artifacts because the running development app locks its normal output directory. The live app has not been restarted and the updated pages have not yet been verified in a browser. Physical NAS and MinIO integrations remain unverified.

Set `CSweet:PublicAppUrl` to the user-facing UI origin when Core is hosted at a different origin; internal publication links use it (falling back to the existing SMTP public-app URL and then the request origin).

No external package repository has been changed in these increments. Additive SDK/contract work must follow AGENTS.md versioning rules when implemented.
