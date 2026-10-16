# Evidence, existing capabilities, and source map

[Overview](README.md) · [Decisions](09-decisions-and-questions.md) · [Acceptance](10-acceptance-scenarios.md)

**Inspection date: 2026-10-15, America/Los_Angeles.** This is a static review of
selected current files and official web references, not a runtime or security audit.

## Repository context

| Repository | HEAD when inspected |
|---|---|
| C-Sweet | `899ce5310c797f36cf2f357caa862308a68ae921` |
| CSweet.Office | `4bc3ba622f8fba7e8c67522482d3b8c63d59afd6` |
| CSweet.Office.Contracts | `4844924a5d81ee4ccdafdce9b4163fe3120d2e1c` |

C-Sweet had pre-existing tracked and untracked work, including Office onboarding
and review/QA changes. Source observations reflect the working tree, not only HEAD.
No deployment, certificate, provider credential, or production billing state was
modified for this documentation. Sibling links assume the documented repository
layout; they resolve locally but require those checkouts when browsing offline.

## Capability and gap map

| Area | Evidence observed | What this proposal still adds |
|---|---|---|
| Office enrollment | One-use claim, identity, certificates, trust pinning | Hosted customer UX and deployment validation |
| No-inbound Office transport | Outbound control and workload streams | Public ingress, proxy behavior, scale/reconnect acceptance |
| Agent isolation | Privileged lifecycle gate, signed hardware-isolated guests | Certification of each actual hosted topology/image |
| Model access | Direct OpenAI-compatible clients and provider profiles | Private connector route and missing native adapters |
| Inference tracking | Attributed receipts, token counts, unknown coverage | Commercial reservation/settlement ledger and invoice reconciliation |
| Windows packaging | Backend/web ZIP and build metadata | Installed configuration, services, clean-machine operation |
| Hosted account service | Not established by inspected sources | Account/subscription/deployment control plane |
| Private model connector | Not established by inspected sources | Standalone service, identity, endpoint policy, stream protocol |
| AWS / budget-host support | General hosting options researched | Actual adapters, bill of materials, benchmarks, operating evidence |

## C01 — Product baseline and packaging

- [Repository README](../../../README.md): developer-preview status, setup, Office split.
- [Implementation index](../../implementation/README.md): historical-plan warnings and current map.
- [Windows build artifacts](../../deployment/windows-build.md): ZIP contents and missing deployment setup.
- [Docker deployment](../../deployment/docker.md): legacy Compose blockers and security requirements.
- [AppHost Program](../../../src/CSweet.AppHost/Program.cs): service composition, dependencies, persistent settings.
- [Windows build script](../../../scripts/release/Build-WindowsDistribution.ps1): distribution packaging foundation.

These establish that current source development is not already a configured SaaS
installation. Do not treat historical Docker-agent plans as current runtime design.

## C02 — Office network and protocol

- [Office network surface](../../../../CSweet.Office/docs/10-system/06-process-network-and-storage-surface.md): no inbound listener, outbound HTTPS and gRPC.
- [Office request/data flow](../../../../CSweet.Office/docs/10-system/05-request-and-data-flow.md): enrollment and assignment sequences.
- [OfficeWorker](../../../../CSweet.Office/src/CSweet.Office.Node/OfficeWorker.cs): authenticated channel, workload relay, reconnect/lifecycle handling.
- [Office Node Program](../../../../CSweet.Office/src/CSweet.Office.Node/Program.cs): HTTPS-only control URL and service wiring.
- [office_control.proto](../../../../CSweet.Office.Contracts/src/CSweet.Office.Contracts/ControlPlane/office_control.proto): `OfficeGateway.Connect`, `OpenWorkloadTunnel`, `DownloadArtifact`; identity, epoch, sequence, and signed assignment fields.
- [OfficeGatewayService](../../../src/CSweet.ExecutionGateway/OfficeGatewayService.cs): `OpenWorkloadTunnel`, `ValidateTunnelFrameAsync` and broker handoff.
- [ExecutionGateway Program](../../../src/CSweet.ExecutionGateway/Program.cs): TLS/client-certificate handling and mapped gateway endpoints.

This supports reusing the Office connection direction. It does not establish a
general reverse proxy for local models or a newly certified public load balancer.

## C03 — Enrollment, recovery, and operations

- [ExecutionFleetService](../../../src/CSweet.Infrastructure/Setup/ExecutionFleetService.cs): `ClaimNodeAsync`, `RecordHeartbeatAsync`, `CreateCertificateRecoveryChallengeAsync`, `RecoverOperationalCertificateAsync`.
- [Fleet operations](../../../Documentation/Operations/DISTRIBUTED_EXECUTION_FLEET.md): connecting, approving, draining, and upgrading Offices.
- [Windows Office installation](../../../../CSweet.Office/docs/40-operations/01-installation-windows.md): supported editions, host prerequisites, service identities, and capacity parameters.
- [Linux Office installation](../../../../CSweet.Office/docs/40-operations/02-installation-linux.md): Ubuntu, cgroup v2, `/dev/kvm`, Firecracker packaging.
- [OfficeOptions](../../../../CSweet.Office/src/CSweet.Office.Node/OfficeOptions.cs): capacity and posture configuration.

Current installer paths and service names are machine-scoped. Multiple tenant
Offices on one OS are not an assumption of this design.

## C04 — Isolation and destination policy

- [Security architecture](../../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md): trusted application versus untrusted execution boundary.
- [Office security invariants](../../../../CSweet.Office/docs/20-security/11-security-invariants.md): trust, identity, authorization, replay, and isolation rules.
- [Office security postures](../../../../CSweet.Office/docs/40-operations/08-security-postures.md): mixed-use, baseline, hardened, and development distinctions.
- [Runtime public internet](../../implementation/features/runtime-public-internet.md): reviewed public egress, private destination denial, and per-provider certification limits.
- [FleetAgentBuildExecutor](../../../src/CSweet.Infrastructure/Setup/FleetAgentBuildExecutor.cs): builder resource requests, including minimum memory.

Public-egress permission is not a private-model connection grant. The model
connector needs its own narrowly scoped destination authority.

## C05 — Provider abstraction and compatibility

- [ILlmProviderFactory](../../../src/CSweet.AI/Providers/ILlmProviderFactory.cs): `IChatClient` creation abstraction.
- [OpenAiCompatibleLlmProviderFactory](../../../src/CSweet.Infrastructure/Llm/OpenAiCompatibleLlmProviderFactory.cs): direct endpoint/client construction and current dispatch rechecks.
- [LlmProviderProfile](../../../src/CSweet.Domain/Setup/LlmProviderProfile.cs): model, base URL, capability, concurrency, and secret-reference settings.
- [LlmProviderTypeExtensions](../../../src/CSweet.Domain/Setup/LlmProviderTypeExtensions.cs): actual compatible-provider set.
- [Provider response compatibility](../../implementation/features/provider-response-compatibility.md): output contracts, bounded formatting recovery, streaming and usage caveats.
- [FileLlmProviderSecretStore](../../../src/CSweet.Infrastructure/Llm/FileLlmProviderSecretStore.cs): file-based JSON secret-store implementation; not managed secret encryption.

The factory rejects provider types outside its compatible set. Native Anthropic,
Azure/Foundry variants, or other protocols need actual supported adapters; naming
them in a profile enum is not enough.

## C06 — Usage and accounting

- [AgentRunLog](../../../src/CSweet.Domain/Setup/AgentRunLog.cs): attempt, employee/work attribution, provider/model, reported usage, and timing fields.
- [Work efficiency and benchmarks](../../implementation/features/work-efficiency-and-benchmarks.md): receipt semantics, cumulative usage, unknown coverage, and telemetry failure behavior.
- [UsageCapturingChatClient](../../../src/CSweet.Infrastructure/Core/UsageCapturingChatClient.cs): model-call usage capture integration point.

Ordinary telemetry can preserve availability when recording fails; a commercial
paid-dispatch path needs a deliberately stronger durable admission/settlement rule.
Do not declare analytics receipts to be a financial ledger without that work.

## Official external references

References were consulted during this planning/documentation conversation on
2026-10-15. Recheck rates, stock, and supported platforms before purchasing or
publishing an offer. A reference is not certification of C-Sweet on that platform.

| ID | Reference | Used for |
|---|---|---|
| E-01 | [AWS nested virtualization](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/amazon-ec2-nested-virtualization.html) | Selected EC2 families and KVM/Hyper-V support |
| E-02 | [AWS Fargate pricing](https://aws.amazon.com/fargate/pricing/) | Published Northern Virginia Linux/x86 example rates |
| E-03 | [Fargate task storage](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-task-storage.html) | Ephemeral storage is not durable customer storage |
| E-04 | [RDS PostgreSQL pricing](https://aws.amazon.com/rds/postgresql/pricing/) | Database costs must be separately sized; no exact RDS quote in this proposal |
| E-05 | [AWS VPC pricing](https://aws.amazon.com/vpc/pricing/) | Network components must appear in the final bill of materials |
| E-06 | [OVHcloud US offers](https://us.ovhcloud.com/deals/) | Advertised budget bare-metal candidates; availability/configuration unverified |
| E-07 | [Hetzner Cloud FAQ](https://docs.hetzner.com/cloud/servers/faq/) | Cloud servers do not expose nested virtualization |
| E-08 | [Azure Dsv6](https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/general-purpose/dsv6-series) | Alternative virtualization-capable family |
| E-09 | [Google nested virtualization](https://docs.cloud.google.com/compute/docs/instances/nested-virtualization/overview) | KVM support, machine restrictions, performance considerations |
| E-10 | [Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve) | Private tailnet web access and HTTPS |
| E-11 | [Tailscale HTTPS certificates](https://tailscale.com/docs/how-to/set-up-https-certificates) | Renewal responsibility for exported certificates |
| E-12 | [Microsoft Hyper-V installation](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/Install-Hyper-V) | Windows/hardware prerequisites |
| E-13 | [Microsoft nested virtualization](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/enable-nested-virtualization) | Separate prerequisite for Office-inside-test-VM scenarios |
| E-14 | [Stripe payments](https://stripe.com/pricing) | Domestic card-fee illustration |
| E-15 | [Stripe Billing](https://stripe.com/billing/pricing) | Subscription-processing fee illustration |
| E-16 | [OpenAI API pricing](https://developers.openai.com/api/docs/pricing) | Future versioned model rate-card source; no fixed default model selected |
| E-17 | [Anthropic pricing](https://platform.claude.com/docs/en/about-claude/pricing) | Model, caching, and regional tariff distinctions |
| E-18 | [Gemini pricing](https://ai.google.dev/gemini-api/docs/pricing) | Model and operation-specific pricing/capability distinctions |

## Limits of this document set

No remote hardware inspection, live provider benchmark, payment, deployment,
public connectivity test, restore drill, or certification was performed to write
these documents. Prices marked proposed are design inputs. Resource sizes and
support costs are assumptions. Acceptance scenarios describe evidence still needed.

Future implementation that changes shared contracts must follow the repository
[contributor rules](../../../AGENTS.md): semantic versions, synchronized downstream
pins, changed-package build/test/pack, and consumer verification without sibling
project references. Agent release notes must match their final manifest versions.
