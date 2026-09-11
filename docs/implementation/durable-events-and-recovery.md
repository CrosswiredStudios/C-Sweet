# Durable events and state recovery

Prefer events to repeated agent polling for asynchronous platform work. An event wakes an agent; the authoritative resource record explains what is true now. Agents may be offline, restart without process-local state, receive duplicates, receive notifications out of order, or miss delivery entirely.

## Platform contract

- Persist a state transition and its outbox notification in the same database transaction. Retrying a failed save or acknowledgement must not create a second logical event.
- Use stable event IDs and resource revisions. Route only to the intended, currently subscribed installation. Event contents confer no authority.
- Keep wake payloads small: resource identity and revision. Resolve current permissions and state when the agent reads the resource. Avoid credentials, access tickets and stale URLs in notifications.
- Deliver through the durable inbox, with retries and leases. Runtime activation may fail independently after inbox persistence; a successful enqueue must survive that failure.
- Preserve authoritative resource state independently of event retention and handler success. Supply a bounded discovery/list API so an agent can recover even without its original operation IDs.
- Do not promise exactly-once delivery or ordered callbacks. Consumers must make side effects idempotent and read current state before reporting, creating tickets or continuing work.
- Prefer event-driven continuation in normal operation. Use a state read on notification and bounded reconciliation on wake/reconnect or when a known operation has become uncertain. Infrastructure delivery workers may check durable queues; agents need not maintain polling loops.

## Web Previews

Agents subscribe to `com.csweet.web-preview.changed.v1`. Its payload is `{ previewId, revision }`. Starting, readiness, renewal, stopping, failure/revocation/expiry and confirmed teardown generate changes. Browser-test completion also wakes the owner. Ordinary HTTP traffic, heartbeats and repeated evidence reads do not generate wake traffic.

The outbox is committed with the preview change. Undeliverable preview wakes remain pending if the installation is disabled or the subscription is not yet approved. Enqueued preview wakes have no one-hour expiration. Handler attempts remain bounded by the ordinary inbox policy; an exhausted handler may dead-letter, and recovery must still work. This does not guarantee that an agent will execute while disabled or without capacity.

On wake, call `list_web_previews` / `WebPreviewClient.ListAsync` for each assigned project. The new `web-preview.list.v1` capability is separately declared and approved. Pages contain this installation's previews, including terminal states; follow `nextAfterId` until null. Listing and reading require current employee/project scope, but do not require the hosting provider to remain enabled. Deleted notification history or expired raw diagnostics does not erase the resource state. Each page is a current read, not a global snapshot; events and subsequent reads cover concurrent changes.

For a known preview, call `read_web_preview` / `ReadAsync` on notification. The response includes its current revision, lifecycle, failure code, expiry and private access reference. It also includes up to twenty browser test runs, with results only when the caller still has test or diagnostic permission; raw results expire after seven days. Listing gives summary records; read each recovered preview to obtain its test-run states. Hosting grants and separate gateway checks still control execution and browser access.

Software Developer 0.9.0 and Video Game Engineer 2.6.0 subscribe and refresh current state through typed callbacks before reporting progress. Their `web-preview.manage.v1` callback includes a `list` action. A late Ready notification therefore cannot make them report an old Ready snapshot after the preview has stopped. Other agent authors can use `WebPreviewAgentEvents.HandleAsync` or their own idempotent handler.

Certified toolchain builds continue using their existing delivery-build lifecycle and APIs. This change does not globally replace unrelated polling paths.

Verification covers atomic rollback, duplicate acknowledgements, quiet transport updates, disabled/unsubscribed recipients, two-week-old wakes, runtime activation failure, replay after inbox persistence, missed-event discovery, production query translation, delayed agent callbacks and diagnostic-permission checks. Existing installations require approval of the new event subscription and list capability; package references alone do not grant them.
