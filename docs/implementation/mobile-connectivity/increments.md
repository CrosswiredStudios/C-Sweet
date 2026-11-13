# Build order and how each step is tested

Updated 2026-11-12. One numbering scheme: **H1–H4** for the first owner-facing deliveries, then **Phases 2–7**. M0–M6 and the earlier increment numbers (1.1 … 8) are retired as schedule labels; the crosswalk at the end maps them.

A step is done when its walkthrough has been run and recorded in the [acceptance register](acceptance-and-operations.md). The owner-facing walkthrough for H1–H4 is in [hosted onboarding](hosted-onboarding.md).

## First deliveries (owner only; no multi-user needed)

| Step | What you receive | Work | How it is tested | Stays off until accepted |
| --- | --- | --- | --- | --- |
| Spikes | Proof the hard parts work. Throwaway. Run alongside H1–H3. | P0-07 (transport, with the production key store and a persisted identity after restart), PN-01 (push), TS-02 (dispatcher entry on a packaged build), **TS-13 (thin-shell prototype)** | Physical phones and a packaged Headquarters. A transport or dispatcher failure stops H4. TS-13 decides whether H4 uses the thin shell or bundled screens. A push failure stops the push promise only. | Not shipped |
| H1 | Fresh install offers Hosted or Private | IL-08, IL-13 | Browser, public network blocked for Private; upgrade unchanged | `Installation:LinkingEnabled` hides Hosted |
| H2 | Register or sign in at the Marketplace and finish setup | IL-01 to IL-05, IL-09, IL-10 | Isolated instances in CI, then **staging**, then the live walkthrough with designated test accounts | Issuer and device endpoints disabled in production until the staging walkthrough passes; then enabled for the live walkthrough |
| H3 | Phone lists My Headquarters | IL-06, IL-07, IL-12, shell entry and chooser (P1-01 to P1-06) | Two phones, two accounts, directory outage and retry | Directory lookup behind the same switch as H2 |
| H4 | Select a Headquarters and read live data over cellular | Relay module, device lifecycle, TS-01, TS-03, TS-04, TS-10 (business list), and either TS-06, TS-14 to TS-16 (thin shell) or TS-F1 (bundled fallback) | Physical phones, inbound closed, revoke ends access. Push is not part of this gate; first approval is from a trusted browser. | Mobile access Disabled for Private and upgrades; Hosted requests Official enrollment |

H2 is the largest of the four: it turns the Marketplace into a sign-in provider. Plan it as several weeks of senior work, not as a form change.

## Phase 2 · Members (can start in parallel with H1)

| Step | Work | How it is tested | Switch |
| --- | --- | --- | --- |
| 2a · Access rules measured | P0-03, MU-01 to MU-03 | Conformance test against the allowlist; adding an unclassified endpoint fails it | None |
| 2b · Every endpoint has a rule and ownership check | MU-05 to MU-07, one folder at a time | Per folder: allowed person, denied person, **wrong-child**, and a normal administrator workflow | None |
| 2c · A second person joins | MU-04, MU-08 to MU-12, using a private local invitation | Browser: invite to Games, Media and its child records unreachable, remove during an open session | `MultiUser:Enabled` (lifecycle in the multi-user guide) |
| 2d · Invited member finds and enrolls from the phone | Recipient binding (MU-Q3), member directory rows | Accept, discover without QR, remove membership, access stops | Same |

## Phase 3 · Devices and push

| Step | Work | How it is tested |
| --- | --- | --- |
| 3a · Remaining enrollment policies and private QR pairing | Phase 3 device issues beyond H4 | Physical phones on staging and on an internal relay |
| 3b · Push end to end | PN-02 to PN-06, PN-08 | One category (`approval`) from Headquarters decision to tap, per app state. This is its own gate; nothing earlier claims push. |

## Phase 4 · Several enterprises on one phone

Profile switching with real data, late responses, reconnect ([Phase 4](phase-4-enterprise.md)). Uses TS-07 (realtime hints), TS-08, TS-09 and the TS-12 harness for day-to-day testing. Two enterprises on different Headquarters versions is a required case.

## Phase 5 · Daily work, one workflow at a time

First: one complete write (TS-05, chat send) with loss-and-recovery proof. Then approvals (after MU-Q1), messages, overview, boards. Each workflow names its real dependencies before it starts: an actor-scoped read, operation key, transfer lane or push. A pilot build lists exactly which workflows it supports.

## Phase 6 · Administration, one capability at a time

Each with a denied-member attempt, an interrupted operation, and a way to switch that capability off.

## Phase 7 · Release

Before the **first external pilot** (not only before general release): the restore drill including a removed member and an outstanding command, revocation, the supported-version check, and the kill switch. Full parity, load and store submission remain general-release gates.

## Rules

- Nothing merges that turns a switch on by default before its step is accepted.
- Evidence kinds are recorded separately and never substituted: fixture, harness, emulator, physical phone, deployed service.
- CI uses isolated services. Rehearsals use staging. Production is touched only for the H2 live walkthrough and later releases.

## Crosswalk for retired labels

| Retired label | Now |
| --- | --- |
| M0 | Spikes |
| M1, M2 | H4 (relay, device lifecycle), remainder in Phase 3a |
| M3 | H4 read, extended in Phase 4 |
| M4 | Phase 5 first write |
| M5, M6 | Phase 7 |
| Increments 1.1–1.3 | Phase 2a–2c |
| Increments 1.4, 1.5 | H2, H1 |
| Increments 2.1–2.4 | Spikes, TS-12 harness, H4, Phase 3 |
| Increments 3.1–3.3 | Phase 1 shell issues, TS-11, H4 |
| Increment 4 (thin-shell trial) | TS-13, alongside H1–H3 |
| Increments 5–8 | Phases 2d, 5, 6, 7 |
