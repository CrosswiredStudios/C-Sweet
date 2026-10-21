# Reusable browser testing compute

## Workload and runtime

`CSweet.Compute.Contracts.ComputeWorkload.BrowserTest` is a reusable alternative to `Command` or
`PublishPort`, exposed by `ComputeMcpTools` through the grant-governed `compute.execute.v1` operation.
`ComputeBrowserTest` requires an exact source commit, build SHA-256, request ID and one to four viewport
widths. It selects a fixed guest runner and derives every guest path from the validated build digest.
Callers stage `build.zip` and an empty `evidence` directory under
`/var/lib/csweet-compute/work/browser-<buildSha256>/`. There is no arbitrary browser URL or host path.

`ComputeBroker.SubmitWorkloadAsync` checks the environment's completed provisioning template snapshot
for `browser-testing-v1`. A newer catalog entry cannot give an existing VM features it never had.
`ComputeDispatchVerifier` independently checks the provider's certified template. Signed workload
digests, scoped grants, operation idempotency, generation fences and result validation use the existing
compute path. The browser is inside a hardware-isolated compute VM. Docker within that VM packages the
tooling; it does not replace the VM isolation boundary.

`build/compute-linux/browser/Dockerfile` pins the official Playwright 1.64.0 Noble image by digest and
locks Playwright/core npm integrity hashes. `provision-guest.sh` builds the guest-local image and runs a
real Chromium/WebGL probe before completing image preparation. The image-build fingerprint includes
the runner and lockfiles. `LocalComputeInstaller.ConfigureAsync` advertises the prepared feature;
`Install-ComputeLocalProvider.ps1` refreshes older installations through normal compute preparation.
Existing VMs keep their original image and provisioning snapshot.

`ComputeBrowserTestExecutor` executes the named browser container and confirms its removal, including
cancellation/timeout paths. Unknown outcomes are never blindly rerun. The container has no network,
read-only input/root, bounded memory/CPU/PIDs and temporary storage. Only the evidence directory is
writable. `extract.py` verifies archive bytes before bounded extraction, rejecting traversal, links,
duplicate/case-colliding paths and missing `index.html`. `runner.cjs` serves only that extracted static
build on container loopback. External browser requests fail rather than acquiring network authority.

## Observations and QA binding

The runner records Chromium version, exact commit/build/request identities and per-viewport startup,
console/page errors, failed HTTP/assets, visible canvas and initialized WebGL evidence. Screenshots,
Playwright traces and `result.json` form a digest-identified evidence ZIP. This is startup smoke coverage,
not gameplay, input interaction, FPS, GPU performance, real-device support or backend-app certification.
Default QA guidance requests widths 375 and 1280; viewport height is 720. WebGL can be disabled explicitly
for other static applications. Assets must be self-contained.

QA 5.1.0 `GameQaBrowserTesting` exposes `test_web_build` in the existing independent QA harness. QA builds
the authorized source locally first; the tool rechecks original source hashes, deterministically archives
the actual output and binds the immutable archive to its authorized commit and source/planning identity.
This proves which independently produced QA build was tested; it does not claim a separately published
release artifact was tested. It never changes tracked source or retargets QA to a mutable URL.

`GameQaBrowserTesting.BrowserState` checkpoints requests before submission, including literal terms,
stable keys, generations and cursors. Lost submission replies replay exactly those terms; known operation
IDs are read only. Build parts and evidence parts survive workspace/runtime replacement in operating
state. After digest verification, screenshots/traces are retained in `GameQaJournal` as paginated
`browser-artifact` observations, independent of the compute VM's lifetime. Current limits are 16 MiB
compressed/64 MiB expanded build, 10,000 files, four widths, 30 seconds per operation and 4 MiB evidence.

`WorkOrchestrator.AcceptComputeWaitAsync` accepts `awaiting-compute` only from the assigned task QA
attempt, bound to an authorized workspace commit, reviewer-owned project environment, current operation
generation and bounded recovery time. It retains the current quality stage without recording a pass,
advancing delivery or spending a failure retry. `ComputeResultReconciler` updates the waiting stage's
retry cursor in the result transaction; existing atomic compute events remain wake hints. Already-arrived
results are detected on wait admission, and the five-minute current-state recovery covers missed wakes.
Agents do not poll. Dispatch and source authorization run again on resume.
The pending review reserves its assignee slot while releasing the agent runtime. Another ticket cannot
advance the shared project browser VM's generation before the first review resumes. Completed compute
wakes do not count against the stage's failure retry budget.

Final QA evidence includes the browser operation and exact build/evidence hashes. Failed browser checks
cannot produce a passing report. `WorkOrchestrator.ValidateBrowserEvidenceAsync` checks those references
against the retained successful compute receipt and exact current tested commit before recording quality
acceptance. Browser observations alone never satisfy unrelated criteria.

## Verification and rollout

Run `node --test tests/browserComputeRunner.test.cjs`,
`python -B -m unittest discover -s tests -p test_browser_compute.py`, the compute/QA wait regressions,
and QA's full suite/self-test. `scripts/Test-BrowserCompute.py` exercises real Chromium at both widths
against authored WebGL, JavaScript error, missing asset, missing WebGL and external-request fixtures.
It retains screenshots/traces under `artifacts/browser-compute`. These local container fixtures verify
the runner; they do not certify an installed Hyper-V VM or a live agent/provider handoff.

Deploy the matching platform, drain obsolete compute instances and prepare the new image through the
normal authorized compute setup. Then publish/install QA 5.1.0 through Installed Agents and review its
new provision/read/execute requests. No network or publication grant is implied. Verify the installed
VM's actual browser workload, lost-response resume and exact-source QA result before retrying the
blocked ticket. Source changes and local package verification do not update installed agents or images.

Local acceptance on October 21, 2026: the final affected platform selection passed 677 tests, with
10 optional PostgreSQL cases skipped; all 107 QA tests, its self-test, 10 image/install preparation
tests, four archive-safety tests and the static-server test passed. All five real Chromium fixtures
passed against the final locked image, including screenshots/traces at 375 and 1280 pixels. QA's
package-only build/pack used SDK 5.0.2 with sibling references disabled; the `.nupkg` metadata was
inspected as `CSweet.Agent.QA.VideoGame` 5.1.0. The installed Hyper-V browser workload and live
end-to-end QA callback remain rollout acceptance, not established by these fixture results.
