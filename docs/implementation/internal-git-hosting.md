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
- LFS object storage supports filesystem and S3-compatible endpoints (including MinIO), repository-scoped keys, atomic filesystem publication and SHA-256/size validation. Authenticated Git LFS batch/basic upload and download APIs are exposed; LFS locks are enforced for Git pushes, agent publications, merges and ref changes.
- Settings source-control page displays effective storage locations, repository readiness, and optional GitHub configuration. Repository readiness probes file creation, flush, rename, and the Git executable; it is not an S3 connectivity or backup check.
- Aspire forwards storage configuration to GitHost. Docker Compose includes GitHost, persistent data and a private shared-key volume. The running local app must be restarted to load changes.

## Business default provider

Managers choose the default provider and approved template on the business source-control page. New businesses and businesses without an explicit setting use the empty internal Git template. A connected Managed GitHub organization with source-access/provisioner installations and an enabled, policy-approved template can be selected instead. Personal GitHub repository creation remains outstanding; the UI does not offer it as ready.

An agent request with an empty template ID resolves the business setting; explicit template IDs keep their provider. New requests retain the resolved connection, template, policy revision and whether the caller used the default. Changing the default affects subsequent requests, not existing repositories or queued/replayed requests. Default and explicit requests cannot reuse the same idempotency key interchangeably. Legacy request rows retain the previous replay rules. An unavailable or disabled selection blocks new creation rather than silently redirecting it to internal Git.

Both internal and GitHub agent provisioning require an active team assignment and capability grant. Workers recheck connection readiness, template approval, team membership and the agent grant before creation. Existing per-connection quotas and approval policies remain authoritative. Manager settings updates use revision checks and audit events.

Apply migration `20260905224730_AddBusinessSourceControlDefaults` before running the updated services. It adds the business settings table and nullable request-origin flag without changing existing repositories. The migration was scaffolded and checked against the model; it has not been applied to the running development database in this task.

## Git client access

Open an internal repository on the business source-control page and expand **Git client access**. Create a named token, save it immediately, and use username `csweet` with that token as the password in the client's credential manager. The page provides the clone URL. Tokens expire after 1–90 days, are shown once, and only their SHA-256 hashes are stored. Active human members can create read-only tokens; managers can create push tokens. Members list/revoke their own credentials, and managers can inspect/revoke all credentials for that repository. Every Git and LFS request rechecks the credential's repository scope, expiration, revocation and current human membership; push also rechecks current manager permission and active repository status.

Clone, fetch, pull, branch/tag pushes and Git LFS batch/basic transfers use standard HTTP endpoints on Core at `/git/{business}/{repository}.git`. GitHost remains private behind signed service authentication. A host-owned pre-receive hook protects active agent branches and disallows unsupported ref namespaces. Default-branch writes require an explicit additional option when creating a manager push token; this is intentional direct human administration, separate from the governed agent merge workflow. Non-fast-forward branch updates are rejected. Internal receipt refs are hidden and cannot be overwritten by Git clients. Credential lifecycle and push transfer attempts are audited; a completed transfer audit event does not by itself imply every ref update was accepted.

Human Git/LFS requires HTTPS, with an exception for direct loopback development connections. Set `CSweet:SourceControl:PublicGitBaseUrl` on Core when its externally accessible origin differs from the request origin. TLS reverse proxies must be explicitly trusted via `CSweet:Http:KnownProxies` (an array of proxy IP addresses); forwarded HTTPS headers from arbitrary clients are not trusted. The bundled nginx configuration forwards `/git/` and preserves the public port. A production reverse proxy must terminate TLS and forward the correct protocol through the configured trusted proxy path; the stock plain-HTTP Docker endpoint alone does not enable remote credential transport.

Current buffered client limits are 128 MiB per Git request or LFS object, and 256 MiB per Git response. The backing LFS storage's larger configured object limit does not override this transport limit. Larger streaming transfers, resumable object upload, and range downloads remain future work. Native Git and Git LFS tests run against an isolated localhost HTTP server; physical NAS/MinIO and production TLS proxy deployments remain unverified.

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
          "RootPath": "/mnt/company-nas/csweet/backups",
          "ExpectedStoreId": "company-backups"
        }
      }
    }
  }
}
```

The default repository root is the current service user's local application-data directory under `CSweet/SourceControl/repositories`. Temporary work defaults to the system temporary directory under `csweet-git`. LFS and backup filesystem locations default to sibling directories of the repository root.

A custom repository root must already contain `.csweet-git-store`, whose text equals `ExpectedStoreId`. Provision this marker on the intended mounted storage using the service account. Missing/mismatched markers fail closed without initializing replacement local storage. Windows UNC paths and mounted SMB/NFS paths are accepted. NAS storage must support exclusive file access, atomic rename and durable writes; verify the actual appliance configuration before use. Run only one active GitHost per repository store.

A separately configured filesystem LFS or backup `RootPath` requires its own `.csweet-object-store` marker and the corresponding `Lfs:ExpectedStoreId` or `Backup:ExpectedStoreId`. S3 uses `AccessKeyId`/`SecretAccessKey` from operator-provided configuration or the AWS SDK's credential chain. Do not commit credentials. Use HTTPS except for loopback endpoints. Buckets must be provisioned and private.

In Docker Compose, `CSWEET_GIT_DATA_VOLUME` defaults to the named volume `csweet-git-data`. It can instead be an absolute NAS mount path, mapped to `/data`. The default named volume receives the image's `repositories/.csweet-git-store` marker. For a NAS mount, provision `/data/repositories/.csweet-git-store` on that share and set `CSWEET_GIT_STORE_ID` to its contents. Mount credentials belong to the host, not to C-Sweet repository records.

GitHost creates its private service key once in `/trust/git.key`; consumers receive this volume read-only. Aspire continues to use its existing generated trusted-service key. Neither mechanism involves an external hosting account.

For relocation: stop writers, copy repositories and LFS objects with metadata, verify Git integrity and LFS hashes, configure the new locations and matching identities, restart, verify reads/writes, then retain the original until the operator explicitly retires it. Changing configuration alone does not migrate data.

## LFS file locking

Native clients can use `git lfs lock <path>`, `git lfs locks`, and `git lfs unlock <path>` following the [Git LFS locking API](https://github.com/git-lfs/git-lfs/blob/main/docs/api/locking.md). The API supports filtered/paginated listing and verification with locks partitioned by authenticated user. Paths are case-sensitive Git paths and locks apply across the repository. Ownership uses the business user's identity, so token rotation does not transfer or release locks. Lock mutations and verification require current push permission; read access permits listing. Only the owner can unlock normally. A business manager with push access may explicitly force unlock another user's lock; native clients use `git lfs unlock --force <path>`.

The repository inspection panel also lists locks, allows managers to acquire locks, and confirms forced release. Mutations are audited. Locks are persisted atomically beside the bare repository under the existing repository write lock, with a 10,000-lock / 16 MiB metadata limit. Lock creation rejects unsafe relative paths and duplicate path ownership. Revoked credentials and inactive/downgraded users are rechecked on every request. Archived repositories expose existing locks for inspection but reject mutations.

Enable Git LFS lock verification in client configuration (`git config lfs.locksverify true`) and use `git lfs track --lockable` for files that need coordinated editing. Verified native Git LFS pushes stop when they change another user's locked asset. GitHost also enforces locks in its trusted pre-receive hook, so disabling client verification or bypassing the pre-push hook does not bypass protection. The authenticated lock owner may update their own locked paths; other callers may not. Checks use literal paths and disable rename detection, external diffs, and text conversion. Existing ref updates compare the before/after trees; new refs compare against their merge base with the default branch (or the empty tree when unrelated); deletion compares against the empty tree. This protects resulting ref contents, not transient edits reverted within unpublished commit history.

Agent publications and governed merges reject changes to any locked path before updating refs. Publications report the locked path without recording a published commit; merges report `file_locked`. Unrelated changes remain allowed. Manager ref creation/update/deletion also checks locks and requires release before changing locked content. Checks hold the same exclusive repository lease as lock mutations; corrupt lock metadata fails closed. Completed publication/merge receipts remain replayable after a lock is acquired. Agent-owned lock acquisition/release capabilities remain outstanding. Repository backups intentionally omit live locks; new restores start unlocked.

## Backup and restore

Business source-control managers can create, list, restore, and explicitly delete backups in the Repository backups panel. Backups use `Storage:Backup` with filesystem/NAS or private S3/MinIO storage. S3 archives use multipart upload where needed. Configure bucket lifecycle cleanup for abandoned multipart uploads after process termination.

A completed backup has a native Git bundle, every LFS object referenced throughout reachable history, and a manifest with a SHA-256 archive digest. It preserves branches, tags, internal operation receipt refs, and the default branch. It excludes repository configuration, hooks, credentials, reflog-only/unreachable objects, workspaces, business metadata, access policies, and database governance records. Back up the application database separately for whole-system recovery. The manifest is published last; incomplete archives are never listed as completed backups. Catalog discovery is independent of repository database records and can list a separately mounted backup store while live repository storage is unavailable.

Restore validates the archive hash, Git object graph, refs, LFS inventory, object sizes, and hashes before making a separate new private repository available. Existing repositories cannot be overwritten. Stable backup/restore request IDs support retries after lost responses; a pending database record retains the restore repository identity. Assign team access after inspecting the restored repository. Git receipt refs are preserved as history, but previous credentials and database approvals are not recreated.

Operations currently run synchronously and are bounded by Git process, HTTP, manifest (4 MiB), archive (10 GiB by default), and catalog (1,000 backups) limits. Large operations may need operator-adjusted timeouts; a timeout may require retrying with the same request identity. There is no automatic retention or deletion. Real NAS and MinIO recovery drills remain required before relying on this for production disaster recovery.

## Remaining implementation

- Provider-neutral routing for GitHub workspace operations beyond preparation. The newly wired operation path currently accepts internal repositories only; GitHub operations that were previously unavailable remain unavailable.
- Extend provider-neutral discovery and the SDK surface beyond the current existing request shape, and remove remaining fixed branch/workflow assumptions. Repository provisioning now uses grants instead of named-agent allowlists and supports an empty internal repository.
- Connection CRUD, broader scoped grants and integrated audit browsing. Team assignment/revocation and existing merge-policy controls are implemented.
- Larger streaming Git/LFS transfers, resumable object uploads, agent lock acquisition/release capabilities, and production TLS proxy verification. Scoped credentials, human Git clone/pull/push and LFS batch/basic transfers are implemented.
- Resumable storage relocation rather than the current operator procedure; background backup jobs, scheduled retention, and paginated backup browsing.
- Personal GitHub repository creation and explicit, resumable migration of Git history/LFS with clean handoff and verified cutover.
- Offline end-to-end agent delivery, actual NAS/MinIO integration tests, rendered browser verification, and full deployment testing.

## Verification

Focused tests cover backup integrity, historical LFS recovery after source deletion, empty repository restore, unavailable backup mounts, restore retry persistence, manager-only backup operations, native Git publication and merge receipts, stale branch rejection, merge conflicts, LFS round-tripping, complete snapshot preparation, dirty refresh/cleanup retries, revoked assignments/team access, repository management authorization, team policy revisions, proposal isolation, exact-SHA governance, native HTTP Git clone/pull/push, protected refs, credential scoping/revocation/expiry, native LFS client round-trips, lock ownership/force-unlock/pagination/revocation and verified-client and bypass-client push rejection, locked publication/merge/ref rejection, on-demand agent provisioning, quota reservations, revoked provisioning grants, recovery identity, and manager policy updates. Application builds use isolated artifacts because the running development app locks its normal output directory. The live app has not been restarted and the updated pages have not yet been verified in a browser. Physical NAS and MinIO integrations remain unverified.

Set `CSweet:PublicAppUrl` to the user-facing UI origin when Core is hosted at a different origin; internal publication links use it (falling back to the existing SMTP public-app URL and then the request origin).

No external package repository has been changed in these increments. Additive SDK/contract work must follow AGENTS.md versioning rules when implemented.
