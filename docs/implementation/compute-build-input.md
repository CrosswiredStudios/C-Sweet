# Resumable static build transfer

`ComputeWorkload.BuildArchive` is an additive, Linux browser-template workload containing
source commit, whole-build SHA-256, byte length and 64-KiB part hashes. The archive limit is
16 MiB. `ComputeBuildInputStore` reads only the submitting installation's staged operating-state
parts and validates all bytes before `ComputeBroker.SubmitWorkloadAsync` admits the operation.

`ComputeBuildInputAccess.ReadAsync` serves parts through the private compute work transport.
It requires a signed current node proof with operation, dispatch and offset, plus current
environment owner, generation, live dispatch lease and execution grants. Input selectors are
rejected by the other provider work endpoints. There are no public object URLs or host paths.

`ComputeBuildArchiveTransfer.ExecuteAsync` verifies the complete input before guest writes.
It uses fixed Python commands, descriptor-relative paths and `O_NOFOLLOW`, compares any partial
prefix, fsyncs and atomically renames a fully verified archive. It does not extract input or run
application code. The dedicated two-minute effect budget is bounded by environment/grant
expiry; ordinary guest command and output limits are unchanged.

The dispatcher may resume this fixed data effect in observation mode with full current grants
and workload binding. Arbitrary commands and browser tests retain their outcome-unknown policy.
`ComputeResultReconciler` accepts only an exact matching `BuildArchiveReceipt`, never a browser
pass. `GameQaBrowserTesting` supports both this path and legacy `qbb` upload state.

Roll out matching core and trusted provider code before QA 5.5.0 (SDK 5.10.0). Preserve provider
enrollment, VM state, image and journal through the normal protected runtime updater. No guest
image change is required. `ComputeBuildTransferTests` verifies tenant/installation/dispatch/grant
fences and malformed input; `tests/compute_build_transfer_probe.py` executes exported commands
in a disposable networkless Linux container to check interruption, conflicts and symlinks.
