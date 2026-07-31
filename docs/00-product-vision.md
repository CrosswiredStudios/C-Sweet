# Product Vision and Operating Model

## Vision

CSweet is a self-hostable operating environment for agent-first companies. It allows a user to act as the CEO of a company or project while a Personal Assistant / Chief of Staff translates executive intent into plans, staffing decisions, delegated work, approvals, and results.

The system combines the organizational clarity and accessibility of a company simulation with real execution through LLMs, tools, MCP servers, vendor services, and human professionals.

## Core experience

The CEO should be able to say:

- “I want to make a game.”
- “Start a clothing company.”
- “Prepare our quarterly financial review.”
- “Build and launch a SaaS product.”
- “Research this market and recommend whether we should enter it.”
- “Hire the team needed to complete this project.”

The Personal Assistant should then:

1. Determine the intended outcome and constraints.
2. Identify the capabilities required.
3. Inspect the company’s current workforce.
4. Recommend or request additional workers where capabilities are missing.
5. Create or delegate creation of a project and task graph.
6. Route tasks to suitable local agents, remote workers, humans, or hybrid services.
7. Monitor progress, cost, quality, dependencies, and risk.
8. Surface decisions and concerns to the CEO.
9. Preserve deliverables, decisions, and operational history.
10. Learn which workforce compositions work best for that company.

## Agent-first workforce

The default assumption is that routine digital work should be attempted by the least expensive capable agentic resource, subject to:

- Quality
- Privacy
- Risk
- Timing
- Compliance
- Credentials
- Availability
- Budget
- Reversibility
- Accountability

Humans should be introduced where they add leverage, including:

- Professional credentials or regulated accountability
- Physical-world work
- High-consequence judgment
- Negotiation and relationship management
- Creative direction and taste
- Ambiguous work with poor verification
- Review or certification of agent-produced work
- Situations where the user explicitly prefers a person

Agent-first does not mean agent-only. A successful plan may combine several resource types.

## Workforce types

CSweet should support four execution types within one organizational model.

### Local Agent

Runs through the self-hosted application using a local or hosted model selected by the user.

Characteristics:

- Can use inspectable, customizable components when their package licenses permit
- Can operate offline when dependencies are local
- Uses application-controlled tools and MCP servers
- Uses company-controlled prompts, policies, and context
- Can be inspected and customized

### Remote Agent

Provided and executed by a third-party workforce provider using proprietary models, software, data, or MCP infrastructure.

Characteristics:

- Requires network access to the provider
- May expose proprietary capability not available locally
- Provider controls execution, licensing, metering, and service availability
- CSweet controls task scope, context authorization, budgets, and audit history

### Human Professional

A real person who joins the platform, publishes services and rates, accepts engagements, and completes assigned work.

Characteristics:

- Must accept an engagement before becoming active staff
- May work hourly, fixed price, by milestone, or on retainer
- May hold verified credentials
- Can collaborate with local and remote agents
- Is asynchronous and cannot be treated as an API call

### Hybrid Service

A provider-managed combination of AI, software, and human review.

Examples:

- AI bookkeeping with human exception review
- Automated tax preparation with CPA review
- Agent-generated design concepts with human art direction
- AI legal research with attorney review

All four can appear in the staff directory, join teams, receive responsibilities, complete tasks, create artifacts, consume budgets, and report blockers.

## Local-first and self-hostable

The core application and Blazor interface should remain self-hostable. No repository license is implied until an explicit license is selected and committed.

A fully local deployment should be able to operate without hosted model access when:

- The selected LLM is local
- Required tools and MCP servers are local
- Only included or locally installed workers are used
- The user does not require the hosted marketplace

The official marketplace and remote commercial workers are optional online services. The core application must remain useful without them.

## Company operating model

The application owns durable company state:

- Company structure
- Departments and teams
- Staff and responsibilities
- Goals, projects, and tasks
- Dependencies and task status
- Budgets and usage
- Decisions and approvals
- Issues and risks
- Work products and artifacts
- Audit and performance history
- Provider connections
- Human engagements
- Company knowledge and policies

LLM conversations are not the system of record. Models reason over application state and propose actions. Application policies validate and persist authoritative state changes.

## Executive communication model

The CEO primarily communicates with the Personal Assistant.

Managers and workers normally report upward through the organization rather than flooding the CEO with raw agent traffic.

The Personal Assistant should provide executive summaries containing:

- Completed outcomes
- Work in progress
- Blockers and risks
- Hiring recommendations
- Budget concerns
- Decisions requiring CEO input
- Recommended next actions
- Material deviations from plan
- Confidence and assumptions

## Capability-driven planning

The system should plan around capabilities rather than immediately mapping goals to conventional job titles.

For example, launching a clothing company may require:

- Market research
- Brand positioning
- Product design
- Pattern making
- Manufacturing coordination
- Photography
- Commerce operations
- Marketing
- Accounting
- Legal review
- Customer support

Some capabilities may be fulfilled by one worker. Others may be assembled from several specialized resources.

A “role” is therefore a convenient bundle of:

- Capabilities
- Responsibilities
- Permissions
- Expected artifacts
- Review criteria
- Accountability
- Communication style

## Responsibility model

Each important task should identify:

- **Execution owner** — performs the work
- **Review owner** — validates the result
- **Decision owner** — resolves open choices
- **Accountable owner** — remains responsible for the outcome

These owners may be agents, people, or providers, but they must be explicit.

## Autonomy model

Autonomy should be assigned per capability rather than globally.

- Level 0: Observe only
- Level 1: Recommend
- Level 2: Draft
- Level 3: Execute after approval
- Level 4: Execute and notify
- Level 5: Execute autonomously within policy

A developer worker might be Level 4 for creating branches but Level 1 for production deployments.

Autonomy should be earned through successful history, not assumed permanently.

## Workforce optimization objective

The system should ask:

> What combination of local agents, commercial services, software, and real professionals can complete this work within the company’s budget, permissions, schedule, quality, privacy, and risk requirements?

The CEO may choose a default policy:

- Cheapest capable workforce
- Fastest completion
- Highest expected quality
- Local-first
- Privacy-first
- Human-reviewed
- Balanced

## Product positioning

CSweet should become an AI-orchestrated operating environment for mixed human and digital companies:

> Build and operate a company where agents perform routine work, specialist services provide advanced capabilities, and people contribute where human expertise matters most.

## Non-goals for the first prototype

- Replacing every project-management product
- Supporting arbitrary untrusted in-process code
- Building a full employment or employer-of-record platform
- Supporting every billing model immediately
- Building an open marketplace before core contracts stabilize
- Allowing unrestricted recursive agent creation
- Automating high-consequence actions without explicit authority
- Building deep simulation mechanics before real execution is reliable
