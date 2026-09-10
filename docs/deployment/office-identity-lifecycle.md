# Office identity and certificate recovery

Office 0.4.0 and Office.Contracts 0.6.0 support unattended renewal and recovery after headquarters or Office has been offline beyond the operational certificate lifetime. Deploy the updated headquarters gateway before upgrading Office. Publish Office.Contracts 0.6.0 before building consumers without the local package feed. No database migration or new enrollment is required for an existing approved Office that retains its original private key.

## Trust and expiry

Operational certificates remain short-lived (24 hours by default). The protected enrolled ECDSA P-256 private key is the durable machine identity. Automatic recovery trusts that identity only while its existing enrollment remains approved. Compromise of this key requires revoking the Office, not merely waiting for a certificate to expire. A revoked, rejected, unapproved, unknown, or key-lost Office requires administrator intervention. Recovery does not approve a node, change its pool, clear drain mode, enable providers, or grant workload capacity.

Office checks renewal every minute while connected. Renewal starts in the last quarter of the certificate lifetime, capped at six hours before expiration, and retries after transient failures. Certificates are verified for time, expected thumbprint, and matching private key before an atomic file replacement. An interrupted response or write is recoverable with a new proof; headquarters returns the already-issued certificate when it remains current.

## Recovery protocol

Office connects through server-authenticated HTTPS, retaining the configured headquarters certificate pin and hostname/time validation. It requests a random 256-bit challenge at `/api/offices/{officeId}/certificate/challenge`. The challenge expires after two minutes and can be consumed once. Office signs the domain-separated Office ID and challenge with its original enrolled private key and posts the proof to `/api/offices/{officeId}/certificate/recover`. No expired client certificate or spent enrollment receipt authenticates this request.

Headquarters verifies the ECDSA P-256/SHA-256 P1363 proof against the stored, signature-checked enrollment CSR. Approval, revocation, and issuance are checked in a serializable database transaction. Successful recovery is audited. Anonymous endpoints use a bounded challenge store, a shared 120-request/minute limiter per gateway, and a 4 KiB recovery-body limit. Failed proofs consume their challenge; clients must request a fresh one.

Challenges are intentionally ephemeral. A gateway restart loses outstanding challenges and the client retries. Deployments with multiple gateways should keep the challenge and recovery requests on the same instance (connection affinity); challenges are never accepted by another instance. No global persistent recovery secret is introduced.

## Live sessions

A new TLS connection must present the exact current, unexpired operational certificate. Once authenticated, the connection is bound to that Office and its enrolled key. Each gateway message still checks approval, revocation, the current operational certificate expiry, and the unchanged enrolled identity. Certificate renewal can therefore extend an already-authenticated connection without stopping running assignments. A stale certificate cannot authenticate a new connection. Office selects the latest certificate for new handshakes, with TLS resumption disabled to avoid reusing stale client credentials.

## Upgrade and verification

1. Deploy headquarters with Office.Contracts 0.6.0 and the new gateway endpoints.
2. Drain Office and wait for zero active assignments before an identity-preserving Office 0.4.0 upgrade, following the installer workflow. Do not delete its identity files or re-enroll it.
3. Start Office and verify a new certificate, fresh heartbeats, and the execution fleet becoming ready. Resume an administrator-drained Office separately when appropriate.

The implementation tests expiry after 30 days, spent bootstrap receipts, replay, altered Office IDs, wrong keys, revoked/unapproved enrollment, drain preservation, lost responses, atomic certificate installation, actual TLS certificate switching, and HTTP/2 connection authentication through renewal and revocation. Windows TLS was exercised locally; Linux/macOS certification and signed installer publishing remain release-workflow checks.
