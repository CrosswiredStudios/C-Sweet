# Workflow parity matrix

Each row is a user outcome to demonstrate on Android and iOS. The entry column names an existing Razor page/component or marks a proposed surface. Find its route with `rg -n '^@page ' src/CSweet.UI/Pages`, then follow the named control into its canonical service. Shared sign-in and enrollment have additional mobile-only rows. `Register`, `Setup` and local root bootstrap/recovery remain installation prerequisites. `LegacyRouteRedirect` aliases lead to the same workflow; `NotFound` is a shell error state.

Actor and permission descriptions specify required effective access for the target design. Confirm the actual authorization member during Phase 0 and add shared service checks where the web baseline is insufficient. Phone capabilities can hide unavailable commands; Headquarters checks every request. Dependencies below are proposed mobile operations over the canonical services. Writes require operation receipts; reads and results require current resource authorization.

The right column specifies the expected evidence. Attach actual run IDs, source revisions and device results in the [single progress register](acceptance-and-operations.md), indexed by row ID. All results are pending. Phase 0 must reconcile nested controls against the current source and add rows for missing or newly shipped actions. A page rendering alone never closes its action rows.

## Account, enterprise and device access

| ID | Web entry or proposed surface | Phone workflow | Actor, required permission and scope | API or transport dependency | Phase | Required acceptance evidence |
| --- | --- | --- | --- | --- | --- | --- |
| A-01 | Login / mobile entry (proposed) | Sign in with a C-Sweet account | Account holder, own identity | Browser OIDC/PKCE and scoped return | 2 | Cancelled login returns to entry; valid login shows only own directory. |
| A-02 | Create account (proposed) | Register and verify email | New person, own address | Identity and test email delivery | 2 | Verified account with no grants reaches empty chooser. |
| A-03 | ForgotPassword / ResetPassword | Recover shared sign-in | Account holder, own recovery proof | Identity recovery | 2 | Expired code fails; new credential restores same subject. |
| A-04 | Mobile entry (proposed) | Sign in as an existing Market customer | Account holder, own identity | Same issuer and account record | 2 | Purchases and roles are those of the original Market ID; no second account is created. |
| A-05 | People & access (proposed) | Review and issue member invitation | HQ admin, assignable enterprise/business grants | HQ invite + outbox | 2 | Recipient and exact grants shown before sending. |
| A-06 | People & access (proposed) | Resend invitation | HQ admin, manage this invitation | Supersede/reissue | 2 | Previous secret stops working. |
| A-07 | People & access (proposed) | Cancel invitation | HQ admin, manage this invitation | Invitation cancellation | 2 | Cancelled token cannot activate. |
| A-08 | Invitation review (proposed) | Accept membership | Invited recipient, matching verified identity | Atomic redemption | 2 | Duplicate accept creates one member. |
| A-09 | People & access (proposed) | Change business or enterprise grants | HQ admin, delegable access | Member revision/update | 2 | A removed business disappears on next read. |
| A-10 | People & access (proposed) | Remove member | HQ admin, manage membership | Removal/outbox | 2 | Existing session loses read, write and event access. |
| A-11 | Private welcome (proposed) | Activate local account from internal invitation | Recipient, single-use internal activation | Pinned HQ local identity | 2–3 | Works with public DNS and internet blocked. |
| A-12 | Private sign-in (proposed) | Sign in with company IdP | Member, registered private issuer | Internal browser OIDC return | 2–3 | Same email at another issuer has no inherited grant. |
| A-13 | Mobile access (proposed) | Select, test and enroll relay | HQ admin, mobile settings | Capability probe + connector binding | 3 | Probe, route and enrollment shown independently. |
| A-14 | Mobile access (proposed) | Change new-phone policy | HQ admin, manage enrollment policy | Revision-checked policy | 3 | Pending request applies current policy after promotion. |
| A-15 | Private pairing (proposed) | Scan or paste device invitation | Member, own authenticated enrollment | Restricted relay ticket + pinned channel | 3 | Camera denied still permits manual entry; expired secret fails. |
| A-16 | Account-discovered enrollment (proposed) | Activate a new phone | Linked member, active invitation/grant | Directory descriptor + restricted ticket | 3 | Normal member activates or waits per policy. |
| A-17 | Devices & requests (proposed) | Approve a phone | Trusted approver, exact pending key at HQ | HQ approval + relay grant | 3 | One grant after lost acknowledgment. |
| A-18 | Devices & requests (proposed) | Deny a phone | Trusted approver, manage request | Terminal request decision | 3 | Late approval cannot reactivate it. |
| A-19 | My devices (proposed) | Rename own phone | Member, own device at HQ | Device label update | 3 | Other sessions display new label. |
| A-20 | My devices / admin device list (proposed) | Revoke an enrolled phone | Owner or authorized HQ admin, target device | Local revoke + durable relay cleanup | 3 | Next HQ action denied while other enterprise still works. |
| A-21 | Device recovery (proposed) | Replace a lost trusted phone | Member and authorized recovery actor, target HQ | Local recovery + new grant | 3 | Lost key stays revoked after replacement. |
| A-22 | Header chooser (proposed) | Switch enterprise | Member, saved or discoverable profile | Discovery, grants and activation scope | 4 | Delayed old reply cannot update destination, including A→B→A. |
| A-23 | Home / Organizations | Open a business from Enterprise | Member, view that organization | Authorized business list and Overview | 4 | Only granted cards and shortcuts render. |
| A-24 | BusinessSwitcher / NavMenu | Change focused business | Member, view destination business | Scoped business list/navigation | 4 | Drawer and subsequent reads target selected business. |
| A-25 | Manage connections (proposed) | Forget a saved profile | Local phone user, selected profile | Protected local storage | 4 | Local key removed, other enterprise unaffected; server grant separately managed. |
| A-26 | NavMenu account menu | Sign out of the account | Member, own active account | Account/session cleanup | 4 | Back cannot reopen signed-out content. |
| A-27 | Settings → C-Sweet account (proposed) | Connect an existing Headquarters to C-Sweet | HQ administrator, installation | Device authorization + installation registration | 2 | Code approved on the C-Sweet site links once; expired code offers a new one. |
| A-28 | Settings → C-Sweet account (proposed) | Disconnect Headquarters from C-Sweet | HQ administrator, local password | Installation retirement | 2 | Local sign-in still works; affected members listed before confirming. |
| A-29 | Notification settings (proposed) | Choose notification categories and quiet hours | Member, own device | Per-device settings at HQ | 5 | Muted category produces no notification; `security` cannot be muted. |
| A-30 | System notification (proposed) | Open the app from a notification | Member, enrolled device | Content-free wake + normal reads | 5 | Opens the named enterprise and list; asks before discarding an unsent edit elsewhere. |

First-run hosting choice and linked administrator registration happen in the Headquarters web UI on its host and are specified in [identity and installation linking](identity-and-installation-linking.md), not in this matrix.

## Daily work

| ID | Web entry or proposed surface | Phone workflow | Actor, required permission and scope | API or transport dependency | Phase | Required acceptance evidence |
| --- | --- | --- | --- | --- | --- | --- |
| W-01 | CommandCenter | Review attention and drill into its source | Member, scoped business overview | Overview/attention reads | 4 | Tap an authorized item and open exact resource. |
| W-02 | CommandCenter | Reorder overview widgets | Member, personal layout scope | Dashboard order write | 5 | Order persists after resume. |
| W-03 | PersonalAssistant | Configure personal assistant | Member, own assistant | Assistant settings write | 5 | Fresh read reflects chosen settings. |
| W-04 | PersonalAssistant | Chat with assistant | Member, own thread | Assistant chat + receipt | 5 | One message persists after interruption. |
| W-05 | Communications | Start a direct conversation | Member, allowed participants in business | Conversation creation | 5 | New thread visible only to its participants. |
| W-06 | Communications | Edit conversation details | Authorized conversation editor | Conversation update | 5 | Reopened chat shows current details. |
| W-07 | Communications | Send a message or mention | Participant, chat send permission | Message command + receipt | 5 | Loss after commit produces exactly one message. |
| W-08 | ConversationMediaUploader | Attach media | Participant, attach to this conversation | Bounded upload + attachment reference | 5 | Cancelled upload cleans partial content; successful digest matches. |
| W-09 | Communications | Mark a conversation/mention read | Recipient, own unread state | Read marker | 5 | Badge clears only for intended recipient. |
| W-10 | Communications | Archive conversation | Eligible participant, target chat | Archive command | 5 | Archive survives refresh without affecting unrelated thread. |
| W-11 | Communications | Retry a failed turn | Authorized participant, retryable turn | Turn operation status | 5 | Retry reports canonical turn outcome. |
| W-12 | Communications | Stop a running turn | Authorized participant, current turn | Stop command | 5 | Terminal result reloads after reconnect. |
| W-13 | Communications | Retry a coordination run | Authorized coordinator, current run | Retry/stop coordination operations | 5 | Eligible failed run returns new status. |
| W-53 | Communications | Stop a coordination run | Authorized coordinator, current run | Retry/stop coordination operations | 5 | Stopped run remains terminal on web. |
| W-14 | Approvals / InlineApprovalCard | Respond to pending decision | Assigned approver, exact decision revision | Decision options and durable receipt | 5 | Concurrent web decision returns current state. |
| W-15 | Approvals / ProjectCreationApprovalCard | Review project creation | Authorized business approver | Project governance command | 5 | One approved or rejected proposal with audit actor. |
| W-16 | HiringWorkflowApprovalCard | Review proposed hire | Authorized hiring approver | Hiring suggestion lifecycle | 5 | Withdrawn suggestion cannot hire. |
| W-17 | ConnectorActionApprovalCard | Review provider action | Authorized connector approver | Connector approval | 5 | Stale options fail before execution. |
| W-18 | DocumentApprovalCard | Review document or artifact decision | Authorized document approver | Artifact decision service | 5 | Decision applies only to selected revision. |
| W-19 | Approvals | Review source-control request | Authorized source-control approver | Source-control decision | 5 | Correct repository and operation recorded. |
| W-20 | Projects | Inspect a project and release evidence | Member, permitted project | Project/read and release evidence | 5 | Release details refer to selected project. |
| W-21 | ProjectSetup | Create project | Member, create project in business | Project setup and validation | 5 | Saved manager/team match accepted setup. |
| W-22 | ProjectSetup | Edit project membership | Project manager, assignable project members | Membership mutation | 5 | Removed member loses project access. |
| W-23 | WorkBoards | Create work item | Board contributor, create permission | Canonical work creation | 5 | Lost response recovers original item. |
| W-24 | WorkBoards | Move item between columns | Board contributor, valid transition | Canonical move | 5 | Touch or keyboard move respects allowed transitions. |
| W-25 | WorkBoards | Transfer item to another board | Contributor, source and target permissions | Transfer command | 5 | Only destination board owns item after transfer. |
| W-26 | WorkBoards | Assign developer | Authorized board editor, eligible worker | Assignment command | 5 | Only selected item gains assignee. |
| W-27 | WorkBoards | Remove assignment | Authorized board editor, current assignment | Unassign command | 5 | Invalidated assignment no longer displayed. |
| W-28 | WorkTrackingPage | Inspect work tracking | Member, work item access | Tracking projection | 5 | Open linked item and see same revision as web. |
| W-29 | WorkBoards | Create sprint | Board planner, sprint scope | Sprint creation | 5 | New sprint is available in correct board. |
| W-30 | WorkBoards | Start orchestrated sprint | Authorized planner, active sprint | Orchestration command/status | 5 | Planned sprint becomes active exactly once. |
| W-54 | WorkBoards | Pause sprint | Authorized planner, active sprint | Orchestration command/status | 5 | Active sprint enters paused state. |
| W-55 | WorkBoards | Resume sprint | Authorized planner, active sprint | Orchestration command/status | 5 | Paused sprint resumes only with current authority. |
| W-56 | WorkBoards | Cancel sprint | Authorized planner, active sprint | Orchestration command/status | 5 | A terminal sprint cannot start new execution. |
| W-31 | WorkBoards | Save work estimate | Authorized planner, board/sprint scope | Planning writes | 5 | New estimate survives reload. |
| W-57 | WorkBoards | Set sprint capacity | Authorized planner, board/sprint scope | Planning writes | 5 | Planning view shows accepted capacity. |
| W-32 | WorkBoards | Carry work over | Planner, affected sprint scope | Carryover command | 5 | Carryover leaves one accountable work item. |
| W-33 | WorkBoards | Edit board columns | Board manager, selected board | Column configuration | 5 | New order survives reload. |
| W-34 | WorkBoards | Publish delivery policy | Authorized board manager | Policy/pipeline revision | 5 | Only new approved revision becomes active. |
| W-35 | WorkBoards | Configure automation grant | Authorized board manager, allowed agent scope | Automation and grant services | 5 | Worker receives only intended board rights. |
| W-36 | WorkBoards | Retry failed work stage | Authorized operator, failed execution | Stage retry/status | 5 | Current stage and failure diagnostics reload. |
| W-37 | WorkBoards | Decide manual stage | Authorized reviewer, current stage | Manual stage decision | 5 | Stale stage cannot complete a second time. |
| W-38 | WorkBoards | Record linked defect | Reviewer, target work item | Defect creation and link | 5 | Source and linked defect match on web. |
| W-39 | WorkBoards | Inspect integrated candidate | Authorized reviewer, current candidate | Candidate read/resubmit | 5 | Reviewer sees only evidence for selected candidate. |
| W-58 | WorkBoards | Resubmit integrated candidate | Authorized reviewer, current candidate | Candidate read/resubmit | 5 | Stale revision fails; current candidate can be submitted. |
| W-40 | WorkBoards | Archive board | Board manager, target board | Board lifecycle | 5 | Archived board disappears from active boards. |
| W-59 | WorkBoards | Restore board | Board manager, target board | Board lifecycle | 5 | Restored board and its items reappear. |
| W-41 | Calendar | Browse calendar and reminders | Member, permitted business calendars | Scoped calendar reads | 5 | Timezone and all-day entries match web. |
| W-42 | Calendar | Save event | Calendar editor, owned/authorized calendar | Event create/update | 5 | Timed and all-day edits survive reload. |
| W-43 | Calendar | Cancel event | Calendar editor, current event | Event cancellation | 5 | Cancelled event has correct final state. |
| W-44 | Calendar | Set business calendar time zone | Authorized calendar editor, business settings | Calendar settings | 5 | Fresh business calendar read uses the saved zone and revision. |
| W-45 | Documents | Find a document | Member, resource visibility | Filter/list/detail reads | 5 | Restricted folder yields no content. |
| W-46 | Documents | Create document | Authorized author, target folder | Document creation | 5 | One document appears with correct parent. |
| W-47 | Documents | Create folder | Authorized folder editor | Folder create | 5 | New folder in expected business. |
| W-48 | Documents | Rename folder | Folder editor, permitted destination | Folder update | 5 | Fresh reads show new label with same parent. |
| W-60 | Documents | Move folder | Folder editor, permitted destination | Folder update | 5 | Source and target trees reflect move. |
| W-49 | Documents | Archive folder | Folder editor, archive authority | Folder archive | 5 | Archival respects domain restrictions. |
| W-50 | Documents | Submit document package | Package author, submittable state | Package submission | 5 | Duplicate submit yields one transition. |
| W-51 | Documents | Accept document package | Package reviewer, current revision | Package decision | 5 | Stale attempt reloads current decision. |
| W-52 | Documents | Archive document package | Authorized package manager | Package archive | 5 | State persists across sessions. |

## Business, people and reporting

| ID | Web entry or proposed surface | Phone workflow | Actor, required permission and scope | API or transport dependency | Phase | Required acceptance evidence |
| --- | --- | --- | --- | --- | --- | --- |
| B-01 | Organizations / NewBusiness | Create business | Authorized enterprise creator | Organization create + onboarding | 6 | One business and correct initial membership. |
| B-02 | Organizations | Edit business | Business administrator, selected organization | Organization update | 6 | Updated card and detail agree. |
| B-03 | Organizations | Delete business | Authorized business administrator | Durable deletion/cleanup status | 6 | Named confirmation, one deletion and visible failed cleanup. |
| B-04 | BusinessOnboarding / ExistingBusinessOnboarding | Resume repository onboarding | Authorized owner, own draft and repositories | Draft analysis + browser authorization | 6 | Interruption resumes same draft and selected sources. |
| B-05 | Benchmarks | Launch benchmark | Authorized operator, benchmark scope | Definition and run creation | 6 | Chosen settings match launched run. |
| B-06 | Benchmarks | Cancel benchmark run | Authorized run operator | Cancellation/status | 6 | Run reaches terminal state on web. |
| B-07 | Benchmarks | Save benchmark review | Authorized reviewer, selected run | Review write | 6 | Review belongs to intended run. |
| B-08 | ExecutiveBriefings | Change briefing schedule | Authorized business operator | Briefing settings | 6 | Schedule persists after reconnect. |
| B-09 | ExecutiveBriefings | Run test briefing | Authorized operator, selected business | Run/test status | 6 | One test result shown. |
| B-10 | Marketplace / Employees | Review and hire employee | Authorized hiring manager, business | Hiring review/command | 6 | Intended role filled once with audit. |
| B-11 | Employees | Change employee role | Authorized manager, assignable role | Role update | 6 | Subsequent actions use changed rights. |
| B-12 | Employees | Remove employee | Authorized manager, target business | Termination command | 6 | Removal reflected in roster and memberships. |
| B-13 | Employees | Edit team roster | Authorized manager, team scope | Team/member commands | 6 | Add and remove confirmed by fresh team read. |
| B-14 | EmployeeDetails | Edit employee profile | Authorized profile editor | Profile update | 6 | Updated profile visible to permitted readers. |
| B-15 | EmployeeDetails | Inspect employee personal board and assignments | Permitted viewer, employee scope | Personal board and workload reads | 6 | Restricted employee data stays hidden. |
| B-16 | EmployeeDetails | Inspect employee usage and timeline | Authorized viewer, selected employee | Usage/audit reads | 6 | Filter remains scoped to employee. |
| B-17 | Employees / EmployeeDetails | Start employee runtime | Authorized runtime operator, target employee | Runtime control/status | 6 | Correct employee transitions to running. |
| B-24 | Employees / EmployeeDetails | Stop employee runtime | Authorized runtime operator, target employee | Runtime control/status | 6 | Stopped employee remains stopped after reload. |
| B-18 | AgentMemory | Search agent memory | Authorized memory viewer, agent scope | Bounded memory reads | 6 | Only granted memories are returned. |
| B-19 | AgentMemory | Review or correct memory claim | Authorized reviewer, selected claim | Memory review service | 6 | Correction is linked to reviewed source. |
| B-20 | AgentMemory | Retry failed memory job | Authorized operator, failed job | Retry/job state | 6 | Next read reports correct job. |
| B-21 | ChiefHiringSettings | Save and apply hiring settings | Authorized manager, installed chief | Hiring configuration | 6 | Active configuration verified at target business. |
| B-22 | Analytics | Explore usage and delivery metrics | Authorized business viewer | Filtered analytics | 6 | Time range and totals agree with web fixture. |
| B-23 | Security | Inspect audit evidence | Authorized business security viewer | Filtered audit detail | 6 | Denied member cannot fetch detail by ID. |

## Administration, integrations and preferences

| ID | Web entry or proposed surface | Phone workflow | Actor, required permission and scope | API or transport dependency | Phase | Required acceptance evidence |
| --- | --- | --- | --- | --- | --- | --- |
| H-01 | Compute | Provision compute instance | Authorized compute operator, business slot | Create/setup job | 6 | Instance ID survives interruption. |
| H-02 | Compute | Update compute service | Authorized operator, business instance | Service update/status | 6 | Chosen revision active on target. |
| H-03 | Compute | Repair failed setup | Authorized operator, failed instance | Repair job | 6 | Recovery progress continues after resume. |
| H-04 | Compute | Release instance | Authorized operator, selected instance | Release and cleanup | 6 | Terminal resource state matches web. |
| H-05 | ExecutionFleet | Register office | HQ host administrator | Fleet office settings | 6 | Named office added under expected Headquarters. |
| H-47 | ExecutionFleet | Edit office settings | HQ host administrator | Fleet office settings | 6 | Changed settings apply only to target. |
| H-06 | ExecutionFleet | Approve fleet enrollment | Host administrator, pending enrollment | Enrollment approval | 6 | Authorized node appears once. |
| H-07 | ExecutionFleet | Create compute pool | Host administrator, targeted pool | Pool create/update/delete | 6 | Pool appears once. |
| H-48 | ExecutionFleet | Edit compute pool | Host administrator, targeted pool | Pool create/update/delete | 6 | Updated selection visible on web. |
| H-49 | ExecutionFleet | Delete compute pool | Host administrator, targeted pool | Pool create/update/delete | 6 | Live dependencies handled before deletion. |
| H-08 | ExecutionFleet | Maintain node | Host administrator, selected node | Node action/status | 6 | Action and labels visible after reconnect. |
| H-09 | ExecutionFleet | Revoke fleet node | Host administrator, node grant | Node revoke | 6 | Revoked node loses access. |
| H-10 | SourceControlSettings | Configure source-control platform | Source-control administrator, installation | Platform setup wizard | 6 | Configured host passes existing validation. |
| H-11 | SourceControl / SourceControlCallback | Authorize repository connection | Authorized source-control operator, business | Browser OAuth + bound return | 6 | Switch enterprise mid-return; binding stays at origin. |
| H-12 | SourceControl | Inspect repository activity | Member, granted repository scope | Repository and activity reads | 6 | Unlinked business cannot read repository. |
| H-13 | PluginSetup | Activate business plugin | Authorized operator, business installation | Setup steps/activation | 6 | All prerequisites checked before activation. |
| H-14 | Plugins | Import plugin bundle | Plugin administrator, installation | Bounded file transfer, preview/install | 6 | Cancelled upload cleans staging; reviewed digest installs. |
| H-15 | Plugins | Apply published plugin update | Plugin administrator, installation | Update review and status | 6 | Updated version matches approved artifact. |
| H-16 | Plugins | Remove plugin | Plugin administrator, installation | Removal and cleanup | 6 | Consequences shown and completion verified. |
| H-17 | Plugins | Manage declared provider configuration | Plugin administrator, permitted provider | Provider settings | 6 | Save/delete through existing validation. |
| H-18 | PluginManage / ManagedPluginReturn | Connect provider account | Authorized connection owner | Provider consent transaction | 6 | Duplicate callback completes once. |
| H-19 | PluginManage | Edit provider account scope | Authorized consent owner, granted business | Account grant revision | 6 | Only intended business grant changes. |
| H-50 | PluginManage | Revoke provider account access | Authorized consent owner, granted business | Account grant revision | 6 | Formerly allowed business loses access. |
| H-20 | PluginManage | Disconnect provider account | Authorized owner, account binding | Disconnect review/cleanup | 6 | Pending cleanup is visible until reconciled. |
| H-21 | ConnectorStandingPolicy | Approve standing policy | Authorized business approver | Connector policy commands | 6 | Authorized scope recorded at Headquarters. |
| H-51 | ConnectorStandingPolicy | Revoke standing policy | Authorized business approver | Connector policy commands | 6 | Future actions cannot use the revoked policy. |
| H-22 | RemoteMcpAccounts | Connect API-key account | Authorized connection owner, target provider | Secret input/account binding | 6 | Stored secret absent from display and logs. |
| H-23 | RemoteMcpAccounts | Rotate API key | Authorized account owner | Protected credential rotation | 6 | Only new key remains usable. |
| H-24 | RemoteMcpAccounts | Grant MCP account use | Authorized owner, target grant | Grant/revoke commands | 6 | Only selected subject gets access. |
| H-52 | RemoteMcpAccounts | Revoke MCP account use | Authorized owner, target grant | Grant/revoke commands | 6 | Grant ceases to authorize actions. |
| H-25 | CommunicationProviderSetup | Link Discord provider | Authorized operator, business connector | Consent/reconcile/link-code lifecycle | 6 | Linked account and business match verified return. |
| H-26 | CommunicationSettings | Disconnect communications provider | Authorized connector owner | Disconnect confirmation | 6 | Only target integration removed. |
| H-27 | ManagedConnections | Select provider connection service | Authorized installation administrator | Existing provider binding/probe | 6 | Changing mobile relay preserves this selection. |
| H-28 | AgentMarketplace | Browse agent catalog | Authorized catalog viewer | Catalog search/filter/read | 6 | Chosen role/category returns expected matches. |
| H-29 | Agents | Preview and install an agent | Authorized agent administrator | Reviewed package install/status | 6 | Preview matches normally installed version. |
| H-30 | Agents | Update installed agent | Authorized agent administrator | Installed Agents update flow | 6 | Approved release and runtime version agree. |
| H-31 | Agents | Set agent schedule | Authorized installation operator | Schedule write | 6 | Schedule and timezone persist. |
| H-32 | Agents | Run agent now | Authorized installation operator | Run command/status | 6 | One triggered execution on reconnect. |
| H-33 | Agents | Retry failed build | Authorized installation operator, failed attempt | Build/start recovery | 6 | Correct build attempt progresses. |
| H-53 | Agents | Retry failed startup | Authorized installation operator, failed attempt | Build/start recovery | 6 | Correct runtime startup is retried. |
| H-34 | Agents | Enable agent installation | Authorized installation administrator | Installation lifecycle | 6 | Enabled state survives resume. |
| H-54 | Agents | Disable agent installation | Authorized installation administrator | Installation lifecycle | 6 | Disabled agent cannot start new work. |
| H-35 | Agents | Remove installation | Authorized installation administrator | Reviewed removal | 6 | Correct installation removed. |
| H-36 | Agents | Edit installed agent configuration | Authorized agent administrator | Installation settings | 6 | Only target installation changes. |
| H-37 | Agents | Assign Chief of Staff | Authorized business operator | Chief assignment | 6 | Assigned role confirmed at named business. |
| H-38 | AgentRuntimeSettings | Change runtime defaults | Host administrator | Runtime configuration | 6 | Validated settings survive reload. |
| H-39 | AgentRuntimeSettings | Recover agent runtimes | Host administrator, installation | Recovery operation | 6 | Recovery status visible after resume. |
| H-40 | ToolchainSetup | Certify build toolchain | Authorized business operator | Profile/image selection and setup job | 6 | Exact tested toolchain becomes ready. |
| H-41 | LlmProviders | Configure LLM provider | Authorized provider administrator | Provider settings and cleanup | 6 | Validated settings apply to chosen provider. |
| H-55 | LlmProviders | Delete LLM provider | Authorized provider administrator | Provider settings and cleanup | 6 | Existing default replacement handled before deletion. |
| H-42 | LlmProviders | Test LLM provider | Authorized provider administrator | Model catalog/test/default | 6 | Test result identifies selected provider. |
| H-56 | LlmProviders | Choose default model | Authorized provider administrator | Model catalog/test/default | 6 | Effective default matches confirmed model. |
| H-43 | GenAiProviders | Configure generative media provider | Authorized provider administrator | GenAI configuration service | 6 | Supported media model settings round-trip. |
| H-44 | LanguageSettings | Choose language | Member, own preferences | User preference writes | 6 | Restart restores selected locale. |
| H-57 | LanguageSettings | Choose time zone | Member, own preferences | User preference writes | 6 | Rendered time reflects selected zone after restart. |
| H-45 | Appearance | Change appearance | User, own presentation settings | Theme preferences | 6 | Theme and contrast verified on both phones. |
| H-46 | AccountSecurity | Regenerate and export recovery codes | Authorized account owner, own recovery | Reauthentication and native export | 6 | Old codes invalidated; export requires explicit action. |

## Document and release detail actions

| ID | Web entry or proposed surface | Phone workflow | Actor, required permission and scope | API or transport dependency | Phase | Required acceptance evidence |
| --- | --- | --- | --- | --- | --- | --- |
| D-01 | ArtifactWorkspace | Save document draft | Author, current artifact edit rights | Draft revision write | 5 | Saved version reopens in the correct business. |
| D-02 | ArtifactWorkspace | Submit revision for review | Author, eligible revision | Revision submit | 5 | Double tap yields one submitted revision. |
| D-03 | ArtifactWorkspace | Save retained evidence | Authorized evidence author | Evidence revision submit | 5 | Evidence persists with intended source. |
| D-04 | ArtifactWorkspace | Request document access | Authenticated requester, visible reference | Access request | 5 | A request is recorded without revealing document content. |
| D-05 | ArtifactWorkspace | Grant document access | Grant manager, selected artifact | Artifact share | 5 | Only the approved recipient gains access. |
| D-06 | ArtifactWorkspace | Revoke document access | Grant manager, active grant | Artifact revoke | 5 | Prior recipient loses content and events. |
| D-07 | ArtifactWorkspace | Move document | Editor, valid destination folder | Artifact move | 5 | Source and destination reads agree after move. |
| D-08 | ArtifactWorkspace | Archive document | Authorized editor, active document | Artifact archive | 5 | Active list excludes archived document. |
| D-09 | ArtifactWorkspace | Restore document | Authorized editor, archived document | Artifact restore | 5 | Restored document visible only to granted readers. |
| D-10 | ArtifactWorkspace | Accept revision | Reviewer, exact submitted revision | Artifact decision | 5 | Newer revision prevents stale acceptance. |
| D-11 | ArtifactWorkspace | Request changes to revision | Reviewer, exact submitted revision | Artifact decision | 5 | Author sees findings against the intended revision. |
| D-12 | Calendar | Dismiss reminder | Recipient, own reminder | Read marker | 5 | Dismissal survives reload. |
| D-13 | ProjectReleases | Activate release plan | Project manager, draft plan | Plan activation | 5 | Only reviewed draft becomes active. |
| D-14 | ProjectReleases | Pause active plan | Authorized plan operator | Plan pause | 5 | State visible on both clients. |
| D-15 | ProjectReleases | Resume paused plan | Authorized plan operator | Plan resume | 5 | Current plan resumes once. |
| D-16 | ProjectReleases | Retry release validation | Authorized operator, eligible blocked execution | Validation retry | 5 | Read status after lost reply. |
| D-17 | ProjectReleases | Reconcile paused planning | Authorized operator, eligible paused plan | Planning reconciliation | 5 | Reload returns canonical planning result. |
| D-18 | ProjectReleases | Recover release execution | Authorized operator, eligible execution | Recovery reason and operation | 5 | Reason and execution match audit. |
| D-19 | ProjectReleases | Review release candidate | Authorized reviewer, current execution | Candidate detail and decision | 5 | Accept or remediation is bound to current evidence. |
| D-20 | ProjectReleases | Cancel remaining plan work | Authorized plan operator | Plan cancellation | 5 | Already committed effects survive cancellation. |

## Inventory review and ownership

Phase 0 owners walk each canonical route, overflow menu and child component with a member and an administrator, including third-party plugin contributions enabled in the test installation. Expand a row into separate rows when authorization, side effects or acceptance differ. For lifecycle controls listed together, record evidence for every offered transition. Preserve row IDs when inserting new coverage. The owner of each Headquarters application service verifies the effective role and the shared access check. The MAUI feature developer verifies the phone interaction and returns a run ID to the register.

Re-run the walkthrough on the release candidate. Include internal navigation from Settings and Personal Assistant onboarding, callback routes and per-resource deep links. Record a host-only prerequisite with its reason and operator procedure in the register. An action available to an authorized web user on a running Headquarters belongs in general-release coverage.
