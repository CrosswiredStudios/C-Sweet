# 12 - Junior Developer Task Checklist

## Purpose

This file gives junior developers a practical checklist for implementing the phased plans. Each task should be small enough to become a GitHub issue.

## General rules

Before starting a task:

- Read the phase document linked from `README.md`.
- Confirm which project the code belongs in.
- Do not add direct infrastructure dependencies to `CSweet.Domain`.
- Do not call LLM providers directly from UI components.
- Do not store secrets in appsettings committed to source control.
- Do not bake secrets into Docker images.
- Add tests for non-trivial logic.
- Update docs when adding or changing behavior.

## Phase 1 checklist

- [ ] Create solution file.
- [ ] Create all initial projects.
- [ ] Add project references.
- [ ] Add `.editorconfig`.
- [ ] Add `Directory.Build.props`.
- [ ] Add `Directory.Packages.props` if using central package management.
- [ ] Add initial README.
- [ ] Add `CSweet.AppHost`.
- [ ] Add `CSweet.ServiceDefaults`.
- [ ] Add `/docker` folder.
- [ ] Add `.dockerignore`.
- [ ] Add `.env.example`.
- [ ] Add API health endpoint.
- [ ] Confirm solution builds.

## Phase 1A Docker checklist

- [ ] Add `docker/api.Dockerfile`.
- [ ] Add `docker/app.Dockerfile`.
- [ ] Add `docker/worker.Dockerfile`.
- [ ] Add `docker-compose.yml`.
- [ ] Add `docker-compose.override.yml` if useful for development.
- [ ] Add named Postgres volume.
- [ ] Add health checks for API, app, worker, and Postgres.
- [ ] Add `docs/deployment/docker.md`.
- [ ] Document LM Studio from Docker using `http://host.docker.internal:1234/v1`.
- [ ] Ensure `docker compose up -d` starts the platform.
- [ ] Ensure the setup wizard loads from Docker.
- [ ] Ensure data persists after container restart.
- [ ] Ensure `docker compose down -v` resets local state.
- [ ] Add CI validation that Docker images build.

## Phase 2 checklist

- [ ] Add EF Core packages.
- [ ] Add Postgres provider package.
- [ ] Create `CSweetDbContext`.
- [ ] Create `SystemConfiguration` entity.
- [ ] Create `LlmProviderProfile` entity.
- [ ] Create `ModelCapabilityTest` entity.
- [ ] Create `OnboardingStep` entity.
- [ ] Create `AuditEvent` entity.
- [ ] Add initial migration.
- [ ] Add first-run seed logic.
- [ ] Add first-run guard endpoint.
- [ ] Add tests for initial setup state.

## Phase 3 checklist

- [ ] Add `CSweet.AI` interfaces.
- [ ] Add provider profile DTOs.
- [ ] Add OpenAI-compatible provider factory.
- [ ] Add LM Studio preset.
- [ ] Add provider connection tester.
- [ ] Add chat completion test.
- [ ] Add streaming test if supported by library.
- [ ] Add structured output test.
- [ ] Add tool calling test.
- [ ] Persist `ModelCapabilityTest` results.
- [ ] Add tests using a fake provider.

## Phase 4 checklist

- [ ] Add `/setup` route in Blazor app.
- [ ] Add setup status API client.
- [ ] Add setup layout.
- [ ] Add welcome step.
- [ ] Add deployment mode step.
- [ ] Add LLM provider setup step.
- [ ] Add model capability test step.
- [ ] Add storage status step.
- [ ] Add worker runtime status step.
- [ ] Add admin setup step.
- [ ] Add finish step.
- [ ] Redirect non-setup routes to `/setup` while setup incomplete.
- [ ] Show Docker-specific LM Studio guidance when running in container mode.

## Phase 5 checklist

- [ ] Add Agent Framework package references after confirming current package names.
- [ ] Create `IAgentRunner`.
- [ ] Create `IAgentWorkflowRunner`.
- [ ] Create `AgentFrameworkAgentRunner`.
- [ ] Create first simple agent profile.
- [ ] Run a prompt through an agent using configured `IChatClient`.
- [ ] Persist an agent run log.
- [ ] Add fake runner tests.

## Phase 6 checklist

- [ ] Add `Organization` entity.
- [ ] Add `OrganizationUser` entity.
- [ ] Add `Role` entity.
- [ ] Add `StrategicObjective` entity.
- [ ] Add `Worker` entity.
- [ ] Add `Task` entity.
- [ ] Add `TaskRun` entity.
- [ ] Add `Artifact` entity.
- [ ] Add `Approval` entity.
- [ ] Add CRUD endpoints for core entities.
- [ ] Add validation.
- [ ] Add tests.

## Phase 7 checklist

- [ ] Add business onboarding UI route.
- [ ] Add organization creation form.
- [ ] Add industry/stage/goal fields.
- [ ] Create default roles.
- [ ] Create first strategic objective.
- [ ] Provision an empty default `To Do -> Done` work board.
- [ ] Register default local strategy worker.
- [ ] Redirect to command center.

## Phase 8 checklist

- [ ] Create `Generate30DayOperatingPlan` use case.
- [ ] Create task command.
- [ ] Create task run state transitions.
- [ ] Build prompt/context assembler.
- [ ] Execute agent.
- [ ] Parse output.
- [ ] Create artifact.
- [ ] Display artifact.
- [ ] Approve/reject artifact.
- [ ] Add task run logs.

## Phase 9 checklist

- [ ] Define worker manifest DTO.
- [ ] Define task execution request DTO.
- [ ] Define task execution response DTO.
- [ ] Add local worker registry.
- [ ] Add worker host health endpoint.
- [ ] Add built-in strategy worker.
- [ ] Execute task via worker host path.
- [ ] Persist worker logs.
- [ ] Add tests.

## Phase 10 checklist

- [ ] Add OpenTelemetry traces.
- [ ] Add structured logging.
- [ ] Add health checks.
- [ ] Add provider health checks.
- [ ] Add worker health checks.
- [ ] Add audit event viewer.
- [ ] Add secret redaction.
- [ ] Add prompt logging policy.
- [ ] Add backup/deployment notes.
- [ ] Add Docker operations notes.

## Pull request expectations

Each PR should include:

- A clear summary.
- Screenshots for UI changes.
- Migration name if database schema changes.
- Test results.
- Manual verification steps.
- Docker verification steps if runtime or configuration changes.
- Any known limitations.

## Suggested PR size

Keep PRs small. A good junior developer PR should usually touch one phase task or one small cluster of related tasks.

Examples of good PRs:

- Add `SystemConfiguration` and initial migration.
- Add `LlmProviderProfile` CRUD endpoints.
- Add LM Studio preset to setup wizard.
- Add provider chat connection test.
- Add Organization entity and create endpoint.
- Add API Dockerfile and Compose health check.

Examples of too-large PRs:

- Add all onboarding, all AI provider support, and all business entities at once.
- Add marketplace support before first local workflow works.
- Rewrite provider abstraction while also changing UI and database schema.
- Add Docker, auth, migrations, and worker execution all in one PR.
