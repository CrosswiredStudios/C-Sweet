# Office attestation — trust boundary decision

**Status:** open decision required · **Severity:** High · **Surfaced by:** runtime security review

## The issue

`CSweet.ExecutionGateway` accepts an Office's self-reported posture and the host trusts it when
deciding whether a workload may run. The values that arrive from the Office and are acted on include:

- the guest image digest,
- `CertificationSuiteVersion`,
- `CertificationEvidenceDigest`,
- `SupportsPublicInternet`,
- the security posture flags consumed by `SecurityPostureAllows`.

`OfficeGatewayService.ReplaceProviderInventory` (≈`CSweet.ExecutionGateway/OfficeGatewayService.cs:269`)
and `ReplaceSecurityPosture` (≈`:330`) overwrite host-side records with whatever the Office sends, and
`ExecutionWorkloadOrchestrator` then admits work on that basis.

## Why it matters

An Office that is compromised, misconfigured, or simply lying can claim a certified image and an
internet-capable posture it does not have, and the host has no independent way to contradict it. The
isolation guarantee ("Office owns isolated execution") becomes an assertion by the party being
isolated.

This is **not** exploitable without control of an enrolled Office — enrolment is a deliberate act — but
it means the security properties are *asserted rather than verified*, which is the difference between
a boundary and a promise.

## Options

| Option | What it costs | What it buys |
|---|---|---|
| **A. Sign the attestation** — Office signs its posture with an enrolment key; host verifies the signature and pins the key per Office. | Key management on enrolment; a format + version field. | Tamper-evidence in transit and at rest. Does not stop an Office lying about its own host. |
| **B. Host-side probing** — host queries the Office for its own image digest / configuration after enrolment and compares with the claim. | A probe endpoint on the Office; a reconciliation job. | Catches drift, not deliberate deception. |
| **C. Require evidence artefacts** — certification evidence digest must resolve to an artefact the host already holds. | Ties enrolment to an evidence store; retro-fits existing Offices. | Turns "we are certified" into "here is the certification we can check". |
| **D. Accept and document** — treat the Office as a trusted component and say so plainly. | Nothing. | Honesty. Also removes the claim of verification. |

## Recommendation

**C + A, in that order.** Evidence artefacts first — they convert the strongest claim
(`CertificationEvidenceDigest`) into something checkable with existing storage. Signature verification
second, so the posture cannot be edited between Office and host by anything on the path.

Option D alone is acceptable only if the documentation and the UI stop implying host-side verification
of Office posture.

## Current mitigation and remaining work

The broker now applies durable `count_within` admission before destructive and outward capabilities
(3 destructive or 60 outward attempts per installation per hour). Count and record are serialized
across PostgreSQL hosts. These are attempt limits, not Office attestation verification.

No certification-recency rule, signature verification or independent host inspection is implemented.
Office claims remain self-reported. The evidence and signature work above remains an open decision.

## Decision

_Owner: product/security._ Record the chosen option here and remove the "open decision" marker.
