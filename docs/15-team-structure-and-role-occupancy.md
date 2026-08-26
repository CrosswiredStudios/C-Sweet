# Team Structure and Role Occupancy

## Status

Working product-design document. This document captures the current direction for teams, roles,
reporting relationships, and executive interaction in C-Sweet. It uses a lean game studio as the
reference scenario, but the model is intended to apply to other kinds of companies and projects.

## Purpose

C-Sweet uses organizational hierarchy as an execution and communication mechanism. Managers
delegate work, workers normally return results through their managers, and authority is constrained
by reporting relationships. A conventional department chart can therefore create unnecessary
communication barriers when a small cross-functional team needs to collaborate closely.

The organizational model should let a CEO begin with an outcome such as "make a game," choose the
leadership roles they personally want to occupy, and have the Chief of Staff propose the smallest
effective team around those choices.

## Core principles

### One initiative should normally be one integrated team

A small game should not begin as separate product, creative, engineering, QA, and marketing
departments. It should begin as one vertically integrated game team with a clear management path.
Specialist departments may become useful after the company has multiple games or enough workers to
justify shared functional management.

### Hierarchy is a delegation and escalation path

The reporting hierarchy determines:

- Who may delegate work to whom.
- Where results and blockers return.
- Who consolidates status.
- Who resolves conflicts within delegated authority.
- When an issue must be escalated.

Hierarchy should not imply that a manager owns every professional decision made by a subordinate.
Domain authority and management authority are distinct.

### Identity and role are different concepts

C-Sweet should distinguish:

- **Identity:** The human, local agent, remote agent, or hybrid worker performing work.
- **Role:** A named responsibility with defined authority, such as CEO, Creative Director, Product
  Manager, or Software Architect.
- **Role assignment:** The assignment of an identity to a role, optionally scoped to a company,
  project, product, or time period.
- **Reporting relationship:** The management relationship used for delegation, communication, and
  escalation.

One identity may occupy multiple compatible roles. In particular, a company owner may be both CEO
of the company and Creative Director of a specific game.

### The user chooses their level of involvement

C-Sweet should not assume that a CEO wants to delegate all domain leadership. In a founder-led
creative company, the CEO will often want to retain creative direction while delegating planning,
coordination, and specialist execution.

The Chief of Staff should establish which leadership roles the user wants to occupy before proposing
the remaining structure.

### The CEO should have a small interface

For a lean game team, the CEO should normally need to interact with only:

- The **Creative Director** about the game, its identity, and consequential creative choices.
- The **Game Product and Project Manager** about priorities, plans, progress, risks, and decisions.
- The **Chief of Staff** about company-level structure, staffing, budgets, and escalations.

When the CEO also occupies the Creative Director role, the Product and Project Manager brings
creative decisions directly to the user in that capacity. C-Sweet should record which role supplied
the authority for a material decision.

## Product and delivery responsibilities

Product management and project or delivery management are separate disciplines even when a lean
company assigns both to one agent.

### Product management

Product management is accountable for maximizing product value. Its responsibilities include:

- Understanding target players, customers, buyers, and markets.
- Framing opportunities as customer or business problems.
- Defining product vision, strategy, desired outcomes, and success measures.
- Ordering the product backlog.
- Defining product scope, business rules, acceptance expectations, and important exclusions.
- Balancing features, quality, technical health, risk, and commercial needs.
- Evaluating adoption and outcomes after release.
- Deciding whether an opportunity should be explored, pursued, deferred, or stopped.

Product management does not own architecture, implementation estimates, test strategy, individual
task assignment, or unilateral delivery commitments.

### Project and delivery management

Project or delivery management is accountable for coordinated execution. Its responsibilities
include:

- Facilitating sprint and milestone planning.
- Maintaining Kanban workflow and operational board hygiene.
- Reconciling estimates, capacity, dependencies, and readiness.
- Tracking blockers, carryover, cycle time, throughput, and velocity.
- Coordinating handoffs, reviews, release activities, and delivery reporting.
- Forecasting delivery without converting estimates into promises.
- Escalating conflicts among scope, time, budget, quality, and capacity.

Project or delivery management does not change product priority, invent specialist estimates,
override QA findings, or make domain decisions on behalf of specialists.

### Lean combination

For a single small game, one **Game Product and Project Manager** may perform both disciplines. Its
operating contract must preserve the boundary between the two modes:

- In product mode, it decides what should be considered next and why.
- In delivery mode, it coordinates what the team can responsibly undertake.
- It cannot use product authority to force an unrealistic delivery commitment.
- It cannot use schedule pressure to silently redefine product value or acceptance.

As the company grows, the combined role may be split into Product Manager and Game Producer or
Delivery Manager positions without changing the underlying responsibilities.

## Kanban and sprint decision rights

The Product Manager owns product content on the board, not every item's execution state.

- The Product Manager orders candidate work and maintains product intent and acceptance criteria.
- The Product and Project Manager facilitates selection against the sprint goal and forecast
  capacity.
- The Architect and Developers own technical decomposition and implementation estimates.
- QA owns validation implications and quality evidence.
- Each executing worker maintains the state of work it owns.
- The delivery team accepts the final sprint contents based on priority, readiness, dependencies,
  and capacity.
- Velocity is a planning and forecasting signal, not an individual performance measure.

## Lean game-team roles

The initial team should contain only the capabilities required by the game. The following roles form
a useful reference model:

| Role | Primary accountability |
| --- | --- |
| CEO | Company strategy, budget, risk tolerance, and final high-consequence approval |
| Chief of Staff | Organization design, workforce planning, executive coordination, and escalation |
| Creative Director | Creative vision, taste, coherence, and consequential creative approval |
| Game Product and Project Manager | Product value, backlog priority, planning, delivery coordination, and consolidated status |
| Game Designer | Mechanics, rules, progression, economy, balance, levels, and player experience |
| Art and Content Agent | Visual direction, asset requirements, content consistency, and initial audio coordination |
| Software Architect | Technical strategy, system boundaries, standards, and cross-cutting technical risk |
| Software Developer | Technical decomposition, estimates, implementation, and unit-level verification |
| Software QA and Release Agent | Test strategy, quality evidence, compatibility, performance, and release recommendation |
| Community and Growth Manager | Store presence, community, launch communications, acquisition, and player-feedback collection |
| YouTube Account Manager | YouTube channel execution under the community and growth strategy |

Game design should remain distinct from product management. The Product Manager defines the target
player outcome and its priority; the Game Designer defines and iterates the gameplay solution.

QA should remain independent of development authority. A delivery manager may coordinate QA work but
cannot alter QA findings. A Product Manager may recommend accepting a known product risk, but
material unresolved release risk requires the authority defined by company policy.

## Reference structures

### Founder-led creative company

The user occupies both CEO and Creative Director roles:

```text
Human user
Roles: CEO; Creative Director for Game A

├── Chief of Staff
└── Game A Product and Project Manager
    ├── Game Designer
    ├── Art and Content Agent
    ├── Software Architect
    │   └── Software Developer agent(s)
    ├── Software QA and Release Agent
    └── Community and Growth Manager
        └── YouTube Account Manager
```

The user supplies creative authority directly. The Product and Project Manager is the operational
controller for the game team and consolidates routine work, status, and decisions.

### Delegated creative direction

The CEO delegates creative authority:

```text
CEO
├── Chief of Staff
└── Creative Director
    └── Game Product and Project Manager
        ├── Game Designer
        ├── Art and Content Agent
        ├── Software Architect
        │   └── Software Developer agent(s)
        ├── Software QA and Release Agent
        └── Community and Growth Manager
            └── YouTube Account Manager
```

The Creative Director becomes the accountable manager for the game as a creative work. The Product
and Project Manager remains the single operational controller for planning and specialist execution.

These structures are templates, not universal answers. The Chief of Staff should adapt them to the
initiative, existing workforce, workload, budget, risk, and the roles the user wants to retain.

## Decision authority reference

| Decision | Accountable authority |
| --- | --- |
| Start, stop, fund, or materially redirect a game | CEO |
| Define the game's creative identity | Creative Director |
| Select the most valuable player or business outcome | Product responsibility |
| Define gameplay mechanics and systems | Game Designer, within creative direction |
| Define visual and content direction | Creative Director or delegated Art and Content lead |
| Establish the ordered product backlog | Product responsibility |
| Propose and facilitate a sprint | Project and delivery responsibility |
| Determine technical architecture | Software Architect |
| Estimate and implement technical work | Assigned Software Developer |
| Determine whether testing supports a quality claim | Software QA |
| Accept material unresolved release risk | Authority established by company policy, ultimately the CEO |
| Define and execute go-to-market activity | Community and Growth, within product and creative direction |
| Propose the reporting and staffing structure | Chief of Staff |
| Approve material organizational or staffing changes | CEO or delegated budget and organization authority |

## Collaboration model

C-Sweet should use manager-as-controller orchestration by default:

1. Executive intent enters through the CEO's chosen leadership contact.
2. The accountable manager translates intent into bounded assignments.
3. Specialists receive the context, authority, dependencies, and acceptance criteria required for
   their work.
4. Results and blockers return to the delegating manager.
5. The manager coordinates review and cross-functional revision.
6. Domain decisions are routed to the role holding the relevant authority.
7. Only material decisions, deviations, or escalations reach the CEO.

The long-term collaboration model should not require artificial departments merely to enable
communication. Project membership, grants, and explicit collaboration relationships may eventually
supplement reporting hierarchy, but they must not bypass accountability, authorization, or audit.

## Scaling rules

- Begin with combined roles where authority conflicts are controlled and workload is small.
- Split Product Manager from Producer or Delivery Manager when delivery coordination becomes a
  sustained workload or schedule pressure begins to distort product decisions.
- Add specialist design, art, audio, analytics, platform, or release roles only when the game needs
  them.
- Create shared functional departments only when multiple game teams require durable specialist
  management.
- Preserve one accountable operational manager for each game or initiative.
- Preserve independent quality evidence and explicit escalation paths as the organization grows.

## Product implications and open questions

- Can one organization user hold multiple scoped role assignments concurrently?
- Can the UI distinguish a person's identity, titles, active roles, and reporting relationships?
- Can a CEO address a descendant directly while results still flow through the accountable manager?
- How is the active decision-making role selected or inferred in a conversation?
- How are temporary project roles activated, delegated, or revoked?
- Which role combinations require warnings because they create authority conflicts?
- Should a project provide a collaboration scope independent of the permanent reporting hierarchy?
- How should C-Sweet represent a combined Product and Project Manager that may later split into two
  positions?
- Which structural changes may the Chief of Staff apply automatically, and which always require CEO
  approval?

## Related documents

- [`00-product-vision.md`](00-product-vision.md)
- [`01-domain-model.md`](01-domain-model.md)
- [`02-agent-orchestration.md`](02-agent-orchestration.md)
- [`16-chief-of-staff-agent-refinement.md`](16-chief-of-staff-agent-refinement.md)
- [`implementation/chief-of-staff-workforce-platform.md`](implementation/chief-of-staff-workforce-platform.md)
