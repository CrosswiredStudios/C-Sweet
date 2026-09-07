# Generic agent collaboration

SDK 3.31.0 adds typed collaboration action helpers over existing coordination APIs:
documentation requests, explicit read sharing, clarification questions/answers, revision-bound
review decisions and handoffs. `Platform.Artifacts.ReadAcceptedAsync` verifies a specific
accepted revision and hash. `CollaborationDependencies.WaitFor` uses durable personal-work
deferral for timed dependency rechecks without holding up other work.

The SDK guide is `../CSweetAgentSdk/docs/collaboration.md` and is included in its NuGet package.
Authoring, capability, grant, security, runtime and template documentation link or describe it.
No new blanket capability grants or event-subscription service were introduced.

Core consumes typed document references and shares document-level read at all three coordination
start modes and participant replies. The author must be a same-organization creator/steward
with current document read access. Exact revision/hash verification precedes all grant writes.
Sharing does not grant edit or approval authority and is not restricted to one revision.

Creative Director 1.6.1 and Producer 2.3.1 use the SDK document reference, sharing, accepted-source
lookup and handoff readiness checks. Their pitch schemas and staffing judgment remain
domain-specific. C-Sweet and both agents pin SDK 3.31.0; consumer tests use the package with SDK
project references disabled and a fresh package cache.

Validation: 139 SDK/sample tests, 64 Creative Director tests, 15 Producer tests, 21 selected host
coordination/document tests, and seven generated-template tests passed. Both game-agent
self-tests and the generated-template self-test passed. Packages are local in
`artifacts/adaptive-packages`; host deployment and agent import are still required for live use.
