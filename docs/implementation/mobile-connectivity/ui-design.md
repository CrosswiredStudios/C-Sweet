# Mobile app and Headquarters screen designs

Maya belongs to Northstar and Harbor through her C-Sweet account. She also uses Studio Lab on its company VPN with a local account. Each enterprise has a business called Games. Use these fixtures in design reviews so reviewers can spot an ambiguous label or a screen carrying the wrong data. The wireframes describe proposed layouts and copy. Use the existing C-Sweet mark, theme tokens, localized strings and business card components.

## Launch, sign in and choose an enterprise

```text
SIGN IN                        ENTERPRISE HOME                 SWITCH ENTERPRISE
┌─────────────────────────┐    ┌─────────────────────────┐    ┌─────────────────────────┐
│ C-Sweet                 │    │ ☰  Northstar ⌄        ◯ │    │ Switch enterprise     × │
│                         │    │                         │    │ maya@example.com        │
│ Sign in to C-Sweet      │    │ Your enterprise         │    │ [Search enterprises   ] │
│ [Continue to sign in]   │    │ Your businesses   [New] │    │                         │
│  Create an account      │    │                         │    │ ✓ Northstar             │
│                         │    │ Games                 ⋯ │    │   Harbor                │
│ Private Headquarters    │    │ Game development        │    │   Studio Lab · Private  │
│ [Scan invitation]       │    │ [Open business]         │    │                         │
│  Enter invitation       │    │ Projects     Employees  │    │ Invitations             │
│                         │    │                         │    │   Atlas · Review        │
│                         │    │ Media                 › │    │ + Connect private HQ    │
│                         │    │ Benchmarks   Settings   │    │   Manage connections    │
└─────────────────────────┘    └─────────────────────────┘    └─────────────────────────┘
```

1. **Launch.** Show the native mark while reading local profiles and protected session references. Finish the splash before checking the network. On a fresh install open Sign in. With an enrolled profile, show its enterprise label and connecting state immediately. Render protected business content after a current authorized read. A storage/key failure offers re-enrollment and access to other profiles.
2. **Shared sign-in.** Continue opens the system authentication browser. Create account opens registration there. Verification, resend, forgotten password and recovery return to the same app transaction. A cancelled browser leaves Sign in usable. With zero memberships show “You haven’t joined an enterprise yet”, a section for pending invitations, and private pairing. Reviewing an invitation shows the named enterprise, intended email/access and Accept or Decline/close as appropriate. Closing the review leaves the invitation pending.
3. **Private sign-in.** Ask for camera permission only after Scan. Manual paste remains available if permission is denied. The preview names the enterprise, exact relay origin and expiry, with Continue and Cancel. A malformed or expired invite offers **Enter another invitation**. Local users activate/sign in through the trusted Headquarters session. Company sign-in opens the configured private IdP. If the relay is unreachable, show **Connect to your company network or VPN**, **Retry**, and **Switch enterprise**.
4. **Select.** Open a sole joined enterprise automatically, present the chooser for multiple on first entry, and restore the last selection on return. Pending invites never auto-enroll the device. Group private profiles by account/deployment; use a second line to distinguish duplicate enterprise names. Mark the current row with a check and an accessible “Current enterprise” label. Show connectivity only when known. Enrolled private profiles are selectable even if shared discovery is unavailable.
5. **Account actions.** Present **Sign out of this account**, **Remove saved connection** and **Revoke this device at Northstar** separately. Explain scope before confirmation. Sign-out dismisses affected sessions and clears linked local account credentials. Removing a connection forgets local credentials for that profile. Revocation requires an acknowledged Headquarters result, recoverable after connection loss.

First-run screens at Headquarters (hosting choice and linked registration) are drawn in [identity and installation linking](identity-and-installation-linking.md). Notification text, tap behaviour and the notification settings screen are specified in the [push guide](push-notifications.md).

## Operate an enterprise

Use a single scrollable column at narrow width. Home retains business mission, industry/stage where available, focus indication, contextual management menu, Projects and Employees shortcuts. **Open business** selects the card's business and opens Overview. **New** opens authorized business creation. If there are no businesses, offer **Create your first business** only to users who can create one. Others see “Ask your enterprise administrator for access to a business.” On connection failure retain the enterprise title with **Retry** and **Switch enterprise**. Empty, loading, failed and unauthorized need different copy.

```text
MENU                           UNSENT EDIT                    LOST RESPONSE
Northstar ⌄                    Switch to Harbor?              Northstar / Games
Enterprise                     Your message in Games          Decision submitted
Personal Assistant             has unsaved changes.           Checking the result…
Focused business [Games ⌄]     [Keep working]                 [Check status]
Overview · Projects            [Discard and switch]           [Switch enterprise]
Source Control · Compute
Approvals · Calendar
Documents · Employees
Analytics · Communications
Security
Settings
```

The header enterprise control is available from every signed-in destination. Place the focused-business control next to its destinations in the drawer. Selecting a business updates that business scope. Selecting an enterprise always opens its Enterprise home. Keep unavailable pilot features out of normal navigation and list pilot coverage in release notes. Explain a compatibility requirement when a supported feature needs a Headquarters upgrade.

On an unsent edit, default focus to **Keep working**. After submission, keep a pending receipt with its original enterprise. Allow switching immediately. On network loss offer a result check and reconnect, with no fresh submission. On a stale approval, show the current revision and require a fresh review. Deep links and browser returns that target another enterprise must identify the destination and pass its access checks before navigation. Clear the previous stack on enterprise change, including native and WebView back history.

## Device enrollment and management

```text
NEW PHONE                       TRUSTED SESSION               MEMBER'S DEVICE LIST
Northstar                       Northstar / Device requests   Northstar / My devices
Waiting for approval            Maya · Pixel                  This iPhone · This device
Pixel · Requested 10:41         Requested 10:41               Last active today
Fingerprint  91…6C              Fingerprint  91…6C            [Rename] [Revoke]
Confirm from a trusted          [Approve]  [Deny]
session or contact your                                       Pixel · Awaiting approval
enterprise administrator.       Activity                      [Cancel request]
[Cancel request] [Switch]       Requested by Maya
```

Show the full fingerprint on demand on both devices. After approval show **Approved, connecting** until both Headquarters and relay complete enrollment. A pending screen recovers on foreground using its request identity. A terminal denial, expiry or cancellation explains who can issue a new request. Revoked access clears protected content and offers another enterprise or re-enrollment. A lost last trusted phone directs the user to an authorized administrator or the local recovery flow.

| Headquarters destination | Layout, primary actions and failure handling |
| --- | --- |
| **Settings → People & access** | Searchable member and invitation lists. Invite form has recipient, identity source, enterprise privilege, business grants, then a review screen summarizing effective access. Return a pending row after successful send. Resend invalidates the old invitation. Expose cancel, edit access and remove with member and enterprise named. On conflicting changes reload current grants before editing again. |
| **Settings → Mobile access** | Separate Provider accounts from Mobile access. Choose Disabled, Official or Self-hosted; private mode takes an exact relay origin. Display service reachability, protocol compatibility, installation enrollment and active route separately. Preview new-device policy before saving. Show affected phones and recovery instructions before changing relay. |
| **Devices & requests** | Separate Pending and Enrolled lists. Each row names the member, label, time, fingerprint and enterprise. Approve/Deny acts on the reviewed request revision. Conflicting or expired requests refresh the row. Revoking an active phone confirms “Revoke Maya’s Pixel at Northstar?” and records who acted. |
| **My devices** | Show current-phone marker, meaningful recent activity, rename and revoke. After own-device revocation, close that profile and return to the chooser. Allow a trusted member to handle their own pairing requests in accordance with Headquarters policy. |

## Interaction review on both platforms

- Test a narrow phone in portrait, landscape, largest supported text size, dark mode and with an on-screen keyboard. Stack actions when labels wrap. Respect safe areas, keep submission controls reachable and allow browser text zoom. Replace the current template viewport restriction in the mobile shell.
- With VoiceOver and TalkBack, verify heading → context → content → primary action order. Label the menu and both selectors by purpose. Announce state transitions once. Restore focus to the launching control after dismissing the sheet and to the destination heading after switching. Use text alongside color and icons.
- Test Back, edge swipe, dialog cancellation, keyboard dismissal and modal focus. Provide list alternatives to board dragging and graph-only navigation. Destructive confirmations and file/provider consent show enterprise, business when applicable, target, and consequence.
- Capture fixture screenshots of zero/one/many memberships, offline Headquarters, same-name businesses, pending device, and permission denial. Store fixture evidence with [Phase 1](phase-1-shell.md). Record physical-device evidence for production flows in the [acceptance register](acceptance-and-operations.md).
