# Web Previews implementation status

## Delivered architecture

The optional CSweet.Plugins.WebPreviews plugin uses independent CSweet.WebHost execution. Office retains agent execution. Product code runs only in certified disposable Hyper-V guests, without an agent/MCP identity. Owner-approved project grants bound source, resources, concurrency, CPU time and lifetime. Defaults: two previews, 2 vCPU/4 GiB/10 GiB each, two-hour hard expiry, 30-minute idle expiry, seven-day diagnostics.

Implemented end to end:

- Durable, transactionally recorded preview-change notifications and browser-test completion wakes, with current-state reads and paginated discovery after missed events. See [events and recovery](durable-events-and-recovery.md).

- Hosting grant requests and preflight, build scheduling through the existing certified toolchain, immutable artifact verification, transactional admission and signed outbound Node dispatch.
- Durable results, reconciliation, stop fencing and independent lease/idle cleanup. Quota remains occupied until protected physical teardown is acknowledged.
- Private browser access with one-use opening tickets, current membership checks, isolated cookies, bounded asset ranges and WebSockets.
- In-place signed renewal within the existing grant's total lifetime and CPU budget. Renewal keeps the same VM and disposable state; access can pause while acknowledgement is pending. It cannot revive an expired instance or change its resources/source. A new approval is needed for larger standing limits.
- Bounded Chromium checks inside the product guest: relative paths, visible selectors and optional text assertions. Jobs are idempotent, limited to ten checks and twenty runs per preview, with no arbitrary agent JavaScript. Failed checks become canonical findings with page/assertion context.
- Sanitized diagnostics, browser errors, seven-day evidence retention and owner-assigned Software QA triage into deduplicated tickets under ordinary board permissions. Copied ticket evidence survives teardown and raw-evidence expiry.
- Optional Web Previews page, owner stop and QA/board/parent configuration. Software Developer 0.9.0 and Video Game Engineer 2.6.0 expose the reviewed web-preview.manage.v1 callback through the separate client library; coding shells do not gain hosting credentials.
- Offline image-building scripts using hash-pinned base and published guest/browser payloads, plus a fresh-host installer with read-only release verification, protected ACLs, separate service identities and service recovery.
- AddPrivatePreviewDispatch migration scaffolded; no database migration applied.

WebHost remains 0.2.0; WebHost.Contracts, WebPreviews and WebPreviews.Client are 0.3.0; Software QA is 0.8.0. BrowserProbe and Client are projects inside existing repositories, not additional repositories. Sibling clones are the default development workflow; publishing to NuGet is unnecessary for local work.

## Deployment and certification gates

The code implementation is present. Operational release still requires building the image in the hardened Linux release environment, running the exact-runtime/image acceptance suite on dedicated Hyper-V hardware, signing the measured release, installing services, configuring wildcard TLS and applying the database migration. Neither unit tests nor a manually asserted certificate establish physical isolation or resource limits. Mandatory certification now includes signed renewal and bounded browser tests.

The old LocalWebPreviewWorker remains development-only during cutover. It is not registered in production. Retire it after the certified replacement passes operational acceptance.

Runtime diagnostics are bounded best-effort collection, not lossless telemetry. Control-plane WebSocket transport adds latency and is intended for demos/testing rather than production multiplayer. Internet egress and public/production deployment are outside this feature.

## Verification

Event/recovery validation passes: 102 focused Headquarters/inbox tests, 78 WebHost tests, 22 Software Developer tests, 16 Video Game Engineer tests and nine Software QA tests. Source integration and fresh-cache package-only checks pass, including agent/plugin self-tests and RuntimeHost/AgentHost builds. All eleven local packages were inspected for versions and dependency pins. The existing migration still matches the model; this event addition requires no extra schema migration. Earlier full Headquarters testing had 1,826 passing, nine skipped and four failures in untouched UI/build-summary tests; hosting tests passed. No package publication, release signing, service installation, database migration or product VM launch occurred.

See [sibling developer setup](../web-previews-development.md), and WebHost docs/image-build.md, docs/enrollment.md and docs/acceptance.md for release steps.
