# GitHub connection wizard

## Entry points and behavior

`SourceControlSettings` shows a compact GitHub card and collapses host storage diagnostics
under **Storage details**. `SourceControlPlatformSetupWizard` opens a small dialog rather
than an inline stepper. New setup defaults to the signed-in personal GitHub account and
offers **Continue to GitHub** without requiring a username. **Use an organization instead**
reveals an optional organization login. Business connections support personal accounts
and organizations independently of the app's owner.
When setup starts from a business, the wizard preserves its ID in browser session storage
across the GitHub visit and offers **Connect GitHub account** back to that business. The
return target is constructed from a parsed GUID; normal business authorization still applies.

`GitHubPlatformWizardFlow.PrepareAsync` starts or resumes the saved session, confirms the
chosen account destination and requested permissions, and prepares the manifest in one action. The
button explains code and pull-request access before recording those confirmations.
`githubWizard.js` submits the generated manifest after render; a native submit button
remains available if browser interop fails. No GitHub app keys are copied by the user.

GitHub's manifest callback returns through settings. For repository business onboarding,
`githubWizard.js.takeDraftId` consumes the tab's saved draft hint and
`SourceControlSettings.OnAfterRenderAsync` resumes that draft automatically before showing
settings controls. `RepositoryBusinessOnboarding` renders the same wizard with `Embedded`
and `DraftId`, finishes pending verification there, and connects the account using the
draft's authorization flow. Other settings entry points reopen the dialog for pending sessions;
`GitHubPlatformWizardFlow.FinishAsync` confirms the server-verified app, continues to
the provisioner approval when requested, and activates only after the required approvals.
Completion reloads the authorized saved session before continuing, including on Retry.
Only a tagged `revision_conflict` triggers one automatic reload/resume; repeated conflicts
retain current state and offer Retry. Completed steps and an already-active session are
reused without registering another app. A replaced, cancelled or expired session stops
completion, and a changed repository-creation choice requires review rather than inheriting
another wizard's consent. `SourceControlSettings.OnAfterRenderAsync` guards overlapping
return-hint reads so the settings wizard cannot race the resumed business wizard.
The initial **Allow C-Sweet to create repositories** checkbox defaults off.
`StartPlatformSourceControlSetupRequest.AllowRepositoryCreation` saves this choice in
`PlatformSourceControlSetupSession.ProvisionerRequested` before leaving for GitHub;
resuming an existing session retains its original choice. Every mutation uses the latest
server revision. Failures retain the last successful step and expose retry. Closing the
dialog retains progress; it does not cancel the session or delete a GitHub app. The
explicit **Discard setup** action under **Advanced setup** retains the existing server
cancellation behavior for recovery from a wrong organization or abandoned handoff.

Repository creation is chosen at the beginning, with no extra enable step on completion.
The wizard launches the separate provisioner app approval on return from the source app
approval. Existing installations can choose it through **Manage GitHub setup**. Verification, administrator checks,
one-time callback state, encrypted credentials, and host validation remain in
`SourceControlPlatformSetupService`; the wizard does not replace these checks.

An empty `ConfirmPlatformOrganizationRequest.OrganizationLogin` selects personal app
registration at `https://github.com/settings/apps/new`; a supplied organization uses
its organization-specific registration endpoint. `GitHubAppManifestClient.ConvertAsync`
requires GitHub's returned `owner.login` and `owner.type`. The callback checks the owner
type against the chosen destination and, for organizations, checks the exact login.
It persists the returned identity in `PlatformGitHubAppCredential.OwnerLogin` and
`OwnerType`. Provisioner callbacks must match the source app's owner. Later provisioning
setup preserves personal registration instead of treating a username as an organization.
The `GitHubAppPersonalOwnership` migration preserves existing credentials as organization
owned, matching the previously supported flow.

`SourceControl` opens a **Connect GitHub** business dialog. GitHub handles account selection
and repository access. The same initial checkbox chooses
`ManagedGitHub` instead of `ExistingGitHub`; the existing callback flow guides both app
installations/authorizations before repository configuration. The choice carries from
platform setup into the business dialog via `allowRepositoryCreation`. If the required
platform apps are not configured, `SourceControl.ContinueConnectionAsync` carries the
choice to administrator setup, which opens the dialog with that choice selected. On return,
pending onboarding reopens the dialog and all available
repositories are selected by default, with **Done** saving the business selection. Users
may narrow the selection. Above the existing 500-repository limit, users must choose a
subset explicitly. The available list remains bounded by the existing provider service.
Existing managed onboarding uses the same dialog, asks for templates and a team, and
collapses naming, quota, and approval settings under **Creation options**.

## Verification

`SourceControlSettings` displays Internal Git storage and `GitHubConnectionCard` account
cards in a responsive grid. `SourceControlPlatformSetupService.GetAccessOverviewAsync`
discovers current source-app installations through `ITrustedSourceControlHostClient.ListInstallationsAsync`,
GitHost's authenticated `/internal/v2/installations`, and `GitHubAppClient.ListInstallationsAsync`
(`GET /app/installations`, bounded to ten pages). Business assignments are additional
metadata, not a prerequisite for showing an installed account. `ListInstallationRepositoryAccessAsync`
verifies current app installation membership and suspension before reading repository grants,
including archived repositories and marking the subset assigned to a business. The read-only
endpoints under `platform-setup/access` and `platform-setup/installations` require
`SourceControlAdministration`. Account cards load repositories automatically.
Configured app controls are collapsed under **GitHub app configuration**. An empty
installation inventory offers the existing app's installation URL, without recreating it.
A standalone GitHub callback with no business state returns to settings only to refresh
the provider-backed inventory; it never trusts the supplied installation ID or creates
a business connection. Stateful business callbacks retain all existing verification.
Revoked grants are never reconstructed from saved repository rows. Failed reads clear
the displayed list and offer refresh. App registration alone is labelled configured,
not a connected account. Creation availability does not force the initial checkbox on;
only a saved pending choice or an explicit incoming choice sets it.

`GitHubPlatformWizardFlowTests` covers combined preparation, revision propagation,
verified-callback activation, explicit provisioner opt-in, interrupted activation retry,
and refusing to activate an unverified handoff. `SourceControlWorkspaceRenderingTests`
covers the compact initial surface and pending onboarding. Existing
`SourceControlPlatformSetupServiceTests` and `SourceControlOnboardingServiceTests`
continue to exercise server authorization, callback verification, and repository scope.

`RepositoryGitHubConnectionFlowTests`, `GitHubSetupReturnTests` and
`PersonalAssistantRenderingTests` cover configured versus unconfigured GitHub, administrator
and operator-managed prerequisites, callback-only draft resumption, inline setup and the
absence of assistant-configuration detours. `node --test tests/githubWizard.tests.mjs`
verifies saving and consuming tab-scoped return hints and replacing abandoned contexts.
`GitHubPlatformWizardFlowTests` also covers stale callback snapshots, competing completion,
bounded conflicts, lost activation responses, changed permissions and failed current-state reads.
