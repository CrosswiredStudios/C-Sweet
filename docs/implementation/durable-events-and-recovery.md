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

## Compute migration

The discarded WebHost proof of concept no longer publishes preview events. Generic compute must follow the platform contract above, including atomic state/outbox persistence and bounded authorized discovery. See [the migration map](compute-migration.md).
