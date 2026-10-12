# Employee Memory workspace

The employee details **Memory** tab embeds `AgentMemory` with `Embedded=true`.
`EmployeeDetails` owns the existing employee identity, portrait, breadcrumb and tab navigation.
The legacy `/organizations/{organizationId}/employees/{employeeId}/memory` route replaces
the current browser history entry with the employee details URL using `?tab=memory`.
Restricted profiles do not instantiate the memory workspace or request its data.

## Presentation and reads

- `AgentMemory.razor` provides Journal, All memory and Connections views, a persistent
  inspector, search and disclosed filters. The outer employee workspace supplies the
  saved Aurora or black/gold appearance; the memory view has no separate theme setting.
- `AgentMemory.Journal.cs` combines the loaded records with authorized per-record revision,
  review and recall responses. Records load in pages of 20. Activity reads run in batches
  of four records, with up to 20 revisions per record. The journal explicitly labels this
  loaded-record window; it is not a complete, globally paginated audit feed. The inspector's
  `MemoryHistoryPanel` retains full version paging and related-record navigation.
- `ReadItemsAsync` preserves applied filters while paging, even when the user has typed an
  unsubmitted search. Journal activity is deduplicated and reused when more records load.
  Partial history failures retain the record list and offer an activity retry.
- Pinned context reads core memory, using the `pinned` metadata emitted by
  `AgentMemoryService.ToItems`. This preserves the pin indicator for confirmed core blocks,
  whose state is `Confirmed`, not `Pinned`. All core memory remains browsable in the library.
- Connections include named, keyboard-operable entity nodes and entity/relationship tables.
  The connection endpoint supports search text; the UI explicitly limits other filters to
  Journal and All memory. Truncated graphs display a notice.

## Management and review

Native disclosures keep management components mounted when closed, preserving in-progress
operation identities. Manage memory contains processing failures and retries,
`MemoryIngestionRecoveryPanel`, `MemoryReextractionPanel`, `MemoryTransferPanel`,
`MemoryErasureStatusPanel`, and system counts/identifiers.

The inspector retains claim correction (including entity-valued replacements), core and
procedure reviews, source evidence, provenance, recall and revision history. Episode
privacy/retention and legacy source review are disclosed separately. Existing server-side
authorization, evidence checks and operation replay semantics remain authoritative.
`MemoryErasureImpactPanel.LockedChanged` prevents selecting another record while an erasure
mutation is in flight or its outcome is unknown. Review callbacks update the visible record,
journal activity and pinned context.

On narrow screens the inspector replaces the list area and provides a close control. View
switching does not discard the selected record. CSS respects reduced-motion preferences.

## Verification

`AgentMemoryJournalTests` covers real persisted activity rendering, unavailable history,
named connections, legacy redirects, filter-stable paging, read-only initial loads, and
pending erasure selection protection. `EmployeeDetailsPageTests` covers tab embedding and
restricted profiles. Existing `Memory*PanelTests` cover the retained mutation workflows.
