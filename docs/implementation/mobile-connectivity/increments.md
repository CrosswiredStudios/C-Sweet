# Increments and how each one is tested

Date: 2026-11-11. The order to build in, so that every step ends with something a person can run and check. Each increment stays behind a switch until its check passes, and none of them changes behaviour for an existing installation until switched on.

Three tracks can run in parallel. Within a track, finish an increment before starting the next.

## Track 1 · Headquarters and accounts (no phone needed)

| # | Increment | Issues | How to test it | Switch |
| --- | --- | --- | --- | --- |
| 1.1 | Access rules exist and are measured | P0-03, MU-01 to MU-03 | Automated: conformance test runs against the allowlist of unannotated endpoints. Nothing visible changes. | None needed |
| 1.2 | Every endpoint has an access rule | MU-05 to MU-07 (one folder at a time) | Automated per folder: allowed person succeeds, Games-only person gets 404 for Media. The allowlist shrinks with each merge; progress is the number left. | None needed; the administrator passes every rule |
| 1.3 | A second person can join | MU-04, MU-08 to MU-12 | In a browser: invite Maya to Games, accept, sign in as Maya, confirm Media is invisible, remove her and confirm access stops. | `MultiUser:Enabled` |
| 1.4 | Market sends email and issues sign-in | IL-01 to IL-06 | In a browser against a staging Market: register, receive the email, sign in; device page approves a test code. Existing Market sign-in still works. | Issuer endpoints off in production until 1.5 passes |
| 1.5 | First run offers Connect to C-Sweet or Private | IL-08 to IL-13 | Fresh Headquarters in a browser: linked path creates the account and administrator; private path makes zero outbound requests (automated); upgrade of an old database is unchanged. | Hosting choice hidden unless `Installation:LinkingEnabled` |

## Track 2 · Connection (no phone needed until 2.4)

| # | Increment | Issues | How to test it | Switch |
| --- | --- | --- | --- | --- |
| 2.1 | Transport and push are proven possible | P0-07, PN-01 | Throwaway spikes on physical phones. Pass or stop. | Not shipped |
| 2.2 | Tunnel works on one machine | TS-01 to TS-05, TS-10, **TS-12** | Desktop harness: dispatcher and local proxy joined by an in-memory stream, no relay, no TLS. Open a desktop browser at the proxy address and use real Headquarters pages as a fixture person. Automated: unmarked route 404, headers stripped. | Harness is a test project; dispatcher not reachable in a normal install |
| 2.3 | Relay carries traffic between two processes | Phase 3 issue 1 (M1) | Two fake Headquarters and two fake phones as console processes exchange test bytes through a local relay; each sees only its own. Then repeat 2.2 through the real relay and TLS, still on desktop. | Relay module not registered by default |
| 2.4 | A real phone pairs and is revoked | Phase 3 issues 2 to 6 (M2), PN-02, PN-03, PN-05 | Physical phones against staging: pair by QR, approve, see **Approved, connecting**, revoke, confirm denial. A push for the `device` category arrives on the approver's phone. | Mobile access = Disabled by default |

## Track 3 · App (emulator until 3.3)

| # | Increment | Issues | How to test it | Switch |
| --- | --- | --- | --- | --- |
| 3.1 | Native shell with fixtures | P1-01 to P1-14 | Emulator and unit tests: every launch case, chooser, unsent-edit warning, Back. Labelled fixture evidence. | Fixtures in Debug only |
| 3.2 | Web app usable at phone width | TS-11 (one navigation group at a time) | Desktop browser at 360 px, then the 2.2 harness. Each group is checked and merged on its own. | None; improves the web app for everyone |
| 3.3 | Shell shows real pages | TS-06 to TS-08 | Emulator on the same machine as Headquarters, through the 2.3 relay: sign in, open an enterprise, see real pages. | Debug builds only |

## Where the tracks meet

| # | Increment | Needs | How to test it |
| --- | --- | --- | --- |
| 4 | **Phone trial** (first full end-to-end) | 1.3, 2.4, 3.3 | TS-09 table on two physical phones over cellular. Decides thin shell or bundled pages. |
| 5 | Shared sign-in on the phone | 1.5, 4, IL-07 | Sign in with a C-Sweet account, see your enterprises, enroll a new phone without a QR code. |
| 6 | Daily work, one matrix row at a time | 4 | Each row: mark the route, add the operation key if tier 1, record evidence on both phones. Suggested order: approvals, messages, overview, boards, then the rest. Push categories (PN-04, PN-06, PN-07) land with approvals and messages. A pilot can ship after any row. |
| 7 | Administration, one matrix row at a time | 6 | Same method. |
| 8 | Release drills | 7 | Phase 7. |

## Rules that keep it testable

- An increment is done when its check has been run and recorded in the [acceptance register](acceptance-and-operations.md), not when its code is merged.
- Nothing merges that turns a switch on by default before its increment passes.
- Fixture, desktop-harness, emulator and physical-phone results are recorded as different kinds of evidence and never substituted for each other.
- Staging needs to exist before 1.4 and 2.4: a Market with the issuer, an official relay and the push gateway, none of them production.

## Added issue

**TS-12 · Desktop tunnel harness — M · Senior-led, juniors use it daily.** A test project that hosts Headquarters in-process, connects the dispatcher to the local proxy with an in-memory duplex stream, signs in as a chosen fixture person, and prints the proxy address. Done when a developer can run one command, open the printed address in a desktop browser and click through marked pages, and when the same harness runs the dispatcher tests in CI. This is the main day-to-day test tool for increments 2.2, 3.2 and 6 before a phone is involved.
