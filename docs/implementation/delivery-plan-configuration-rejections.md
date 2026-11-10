# Delivery plan configuration rejections

## What happened (Super Awesome Games, Prism Break, 2026-11-10)

Production Sprint 1 was planned but never started. A sprint starts only after the Producer configures and activates the
project's delivery plan (`work.delivery.configure.v1`, then `work.delivery.control.v1`). Every configuration failed with
"Provide a name and distinct workflow tickets."

The Agent SDK named the increment plan `"Increment: "` plus the epic title, which came to 204 characters. The limit is
160. The one message covered four rules (blank name, name over 160, more than 500 workflows, repeated tickets), so the
cause was not visible. The broker reported the rejection as `Conflict`, the same code as a revision race, so the Producer
treated it as a temporary wait and re-ran the identical request every 7–10 minutes from two commitments: about 1,000
capability calls in 40 minutes, with no one told.

## Behaviour now

- **One message per rule.** `WorkDeliveryService.ConfigureTicketsAsync` reports the plan name length and the limit, the
  workflow count and the limit, or the repeated ticket ids. Limits are `WorkDeliveryService.MaxPlanNameLength` (160)
  and `MaxTicketWorkflows` (500).
- **Races are marked retryable.** `WorkDeliveryCapabilityHandler` still returns `Conflict` for both cases, so existing
  SDKs behave as before. It now sets `Retryable = true` for `DbUpdateConcurrencyException` (a revision race, which can
  succeed after a re-read) and `Retryable = false` for `ArgumentException`, `InvalidOperationException` and
  `JsonException` (the request itself is rejected). The flag reaches agents as `_meta.csweet.retryable` and
  `PlatformCapabilityException.Retryable`.
- **SDK 5.16.0** keeps generated increment names within 160 characters and sets
  `DeliveryStaffingException.RejectedByPlatform` when a plan configuration is rejected rather than raced.
- **Producer 6.5.0** escalates a rejected plan to its manager once per board and reason, then rechecks hourly. The
  manager can reply `Retry staffing: <what changed>` to recheck at once.

## Recovery

A retry reuses the workflow tickets that earlier attempts created, because the broker replays a ticket create by its
idempotency key. After the SDK and Producer update, the stalled sprint starts on the Producer's next recheck, or at once
after `Retry staffing: …`. No data repair is needed.

## Not covered

- Other broker handlers that map `ArgumentException` to `Conflict` do not set `Retryable` yet.
- No handler-level test covers the `Retryable` mapping; `TicketWorkflowTests` covers the new messages.
- The platform's own `CSweet.Agent.SDK` package reference (`Directory.Packages.props`, 5.14.0) is unchanged; it is used
  only by tests.

Validation: not yet run.
