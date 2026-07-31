using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AI.Providers;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Communications.Abstractions;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Chat;

public sealed class ChatTurnWorker(
    IServiceScopeFactory scopeFactory,
    IChatStreamRouter outputRouter,
    IChatTurnEventRouter eventRouter,
    IOptions<ChatTurnOptions> options,
    ILogger<ChatTurnWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("CSweet.Application.ChatTurns");
    private static readonly Counter<long> TurnCompletions = Meter.CreateCounter<long>("csweet.chat.turns.completed");
    private static readonly Counter<long> TurnFailures = Meter.CreateCounter<long>("csweet.chat.turns.failed");
    private static readonly Histogram<double> TurnDuration = Meter.CreateHistogram<double>("csweet.chat.turn.duration", "ms");
    private static readonly Histogram<double> FirstOutputLatency = Meter.CreateHistogram<double>("csweet.chat.turn.first_output", "ms");
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var turns = scope.ServiceProvider.GetRequiredService<IChatTurnService>();
                var turnId = await turns.ClaimNextAsync(_leaseOwner, stoppingToken);
                if (!turnId.HasValue)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                await ProcessAsync(scope.ServiceProvider, turnId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Chat turn worker processing pass failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(IServiceProvider services, Guid turnId, CancellationToken stoppingToken)
    {
        var turns = services.GetRequiredService<IChatTurnService>();
        var db = services.GetRequiredService<CSweetDbContext>();
        var memory = services.GetRequiredService<IAgentMemoryService>();
        var conversations = services.GetRequiredService<IConversationService>();
        var runtime = services.GetRequiredService<IAgentInteractiveRuntimeService>();
        var configurations = services.GetRequiredService<IAgentInstallationConfigurationService>();
        var inbox = services.GetRequiredService<AgentWorkInbox>();
        var audit = services.GetRequiredService<IAuditEventWriter>();
        var turn = await db.ChatTurns.Include(x => x.UserMessage).Include(x => x.Conversation)
            .SingleAsync(x => x.Id == turnId, stoppingToken);
        var conversation = turn.Conversation!;
        var userMessage = turn.UserMessage!;

        using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        hardTimeout.CancelAfter(options.Value.HardTimeout);
        try
        {
            await PublishTraceAsync(turns, turnId, "system", turn.Attempt > 1 ? "turn.restarted" : "turn.started", "running", turn.Attempt > 1 ? "Turn restarted" : "Turn started",
                "The durable turn worker accepted this request.", new { turn.Attempt }, cancellationToken: hardTimeout.Token);

            var recallWatch = Stopwatch.StartNew();
            await PublishTraceAsync(turns, turnId, "memory", "recall.started", "running", "Searching memory",
                "Searching relationship, employee, and organization memory namespaces.", cancellationToken: hardTimeout.Token);
            string? recalledMemory;
            using (var memoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(hardTimeout.Token))
            {
                memoryTimeout.CancelAfter(options.Value.MemoryOperationTimeout);
                try
                {
                    recalledMemory = await memory.RecallForConversationAsync(conversation.Id, userMessage.Content, memoryTimeout.Token);
                }
                catch (OperationCanceledException) when (memoryTimeout.IsCancellationRequested && !hardTimeout.IsCancellationRequested)
                {
                    recalledMemory = null;
                    await PublishTraceAsync(turns, turnId, "memory", "recall.bypassed", "warning", "Memory search bypassed",
                        $"Memory did not respond within {options.Value.MemoryOperationTimeout.TotalSeconds:g} seconds. Continuing with the original message.",
                        cancellationToken: hardTimeout.Token);
                }
            }
            recallWatch.Stop();
            await PublishTraceAsync(turns, turnId, "memory", "recall.completed", "completed", "Memory search complete",
                string.IsNullOrWhiteSpace(recalledMemory) ? "No relevant memories were selected." : recalledMemory,
                new { selected = !string.IsNullOrWhiteSpace(recalledMemory) }, "Personal", recallWatch.ElapsedMilliseconds, hardTimeout.Token);

            try
            {
                using var memoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(hardTimeout.Token);
                memoryTimeout.CancelAfter(options.Value.MemoryOperationTimeout);
                await memory.CaptureMessageAsync(userMessage.Id, cancellationToken: memoryTimeout.Token);
                await PublishTraceAsync(turns, turnId, "memory", "capture.completed", "completed", "User episode captured",
                    "The original user message was captured without modifying the persisted chat message.", cancellationToken: hardTimeout.Token);
            }
            catch (OperationCanceledException) when (!hardTimeout.IsCancellationRequested)
            {
                await PublishTraceAsync(turns, turnId, "memory", "capture.deferred", "warning", "User capture deferred",
                    "Memory capture timed out. Chat is continuing without waiting for it.", cancellationToken: hardTimeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await PublishTraceAsync(turns, turnId, "memory", "capture.deferred", "warning", "User capture deferred",
                    exception.Message, cancellationToken: hardTimeout.Token);
            }

            var installationId = await conversations.GetAgentInstallationIdForEmployeeAsync(turn.TargetAgentOrganizationUserId, hardTimeout.Token)
                ?? throw new InvalidOperationException("The agent employee is not linked to an installation.");
            var configuration = await configurations.GetAsync(installationId, hardTimeout.Token);
            var configuredProviderId = GetConfiguredProviderId(configuration);
            var providerId = configuredProviderId.HasValue && await conversations.IsProviderProfileEnabledAsync(configuredProviderId.Value, hardTimeout.Token)
                ? configuredProviderId
                : await conversations.GetDefaultProviderProfileIdAsync(hardTimeout.Token);
            if (!providerId.HasValue) throw new InvalidOperationException("No enabled LLM provider is configured.");

            var output = new System.Text.StringBuilder();
            Guid? terminalResourceChangeRequestId = null;
            var bypassMemory = false;
            string? fallbackReason = null;
            var memoryWasRecalled = !string.IsNullOrWhiteSpace(recalledMemory);
            var conversationPrompt = ChatPromptPolicy.BuildConversationPrompt(recalledMemory, userMessage.Content);
            var agentPrompt = ChatPromptPolicy.BuildPrimaryAgentPrompt(conversation.Id, turnId, conversationPrompt);
            try
            {
                var readiness = await runtime.EnsureReadyAsync(installationId, hardTimeout.Token);
                if (!readiness.IsReady) throw new InvalidOperationException(readiness.Reason ?? "The agent runtime is not ready.");

                await turns.SetStatusAsync(turnId, ChatTurnStatus.Dispatching.ToString(), cancellationToken: hardTimeout.Token);
                outputRouter.BindAlias(conversation.Id, turnId);
                var reader = outputRouter.Subscribe(turnId);
                var payload = new UserMessageReceived(
                    providerId.Value, conversation.Id.ToString(), conversation.InitiatedByOrganizationUserId.ToString(), agentPrompt, null, turnId, turn.Attempt, turn.UserMessageId);

                await PublishTraceAsync(turns, turnId, "model", "model.dispatched", "running", "Assistant dispatched",
                    "The request was submitted as durable agent work.", new
                    {
                        providerProfileId = providerId,
                        model = GetConfiguredString(configuration, "llmModel"),
                        installationId
                    }, cancellationToken: hardTimeout.Token);
                var work = await inbox.EnqueueAsync(
                    conversation.OrganizationId.ToString("D"),
                    installationId,
                    CSweet.Domain.Setup.AgentWorkKind.Event,
                    AgentChatEvents.UserMessageReceivedEvent,
                    JsonSerializer.SerializeToElement(payload, JsonOptions),
                    $"chat-turn:{turnId:D}:attempt:{turn.Attempt}",
                    DateTimeOffset.UtcNow.Add(options.Value.HardTimeout),
                    correlationId: turnId.ToString("D"),
                    causationId: turn.UserMessageId.ToString("D"),
                    sourceType: "chat-turn",
                    sourceId: turnId.ToString("D"),
                    maximumAttempts: 3,
                    cancellationToken: hardTimeout.Token);
                _ = PumpAgentWorkAsync(work.Id, turnId, turn.Attempt, hardTimeout.Token);
                await turns.SetStatusAsync(turnId, ChatTurnStatus.Running.ToString(), cancellationToken: hardTimeout.Token);

                var pendingOutput = new System.Text.StringBuilder();
                var outputFlush = Stopwatch.StartNew();
                await using var chunks = reader.ReadAllAsync(hardTimeout.Token).GetAsyncEnumerator(hardTimeout.Token);
                while (true)
                {
                    if (!await chunks.MoveNextAsync()) break;
                    var chunk = chunks.Current;
                    if (chunk.Attempt != 0 && chunk.Attempt != turn.Attempt) continue;
                    if (await db.ChatTurns.AsNoTracking().AnyAsync(x => x.Id == turnId && x.Status == ChatTurnStatus.Cancelled, hardTimeout.Token))
                        return;
                    if (!string.IsNullOrWhiteSpace(chunk.Error)) throw new InvalidOperationException(chunk.Delta);
                    if (TryGetTerminalResourceChangeRequestId(chunk, out var resourceChangeRequestId))
                    {
                        terminalResourceChangeRequestId = resourceChangeRequestId;
                        await PublishTraceAsync(turns, turnId, "output", "output.durable-approval", "completed",
                            "Hiring plan approval requested",
                            $"The durable approval request {resourceChangeRequestId:D} is the terminal response for this turn.",
                            new { resourceChangeRequestId }, cancellationToken: hardTimeout.Token);
                        break;
                    }
                    if (chunk.Kind == "progress")
                    {
                        await PublishTraceAsync(turns, turnId, "model", "agent.progress", "running", chunk.Delta,
                            details: chunk.Metadata, cancellationToken: hardTimeout.Token);
                        if (chunk.IsFinal) break;
                        continue;
                    }
                    if (!chunk.IsFinal && chunk.Delta.Length > 0)
                    {
                        output.Append(chunk.Delta);
                        pendingOutput.Append(chunk.Delta);
                        if (pendingOutput.Length >= 512 || outputFlush.Elapsed >= TimeSpan.FromMilliseconds(250))
                        {
                            var delta = pendingOutput.ToString();
                            pendingOutput.Clear();
                            outputFlush.Restart();
                            await turns.AppendOutputAsync(turnId, delta, hardTimeout.Token);
                            await PublishTraceAsync(turns, turnId, "output", "output.delta", "running", "Assistant output",
                                delta, cancellationToken: hardTimeout.Token);
                        }
                    }
                    if (chunk.IsFinal) break;
                }
                if (pendingOutput.Length > 0)
                {
                    var delta = pendingOutput.ToString();
                    await turns.AppendOutputAsync(turnId, delta, hardTimeout.Token);
                    await PublishTraceAsync(turns, turnId, "output", "output.delta", "running", "Assistant output",
                        delta, cancellationToken: hardTimeout.Token);
                }
                if (output.Length == 0 && terminalResourceChangeRequestId is null)
                    throw new InvalidOperationException("The model provider returned an empty response.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException && output.Length == 0)
            {
                logger.LogWarning(exception, "Agent work failed before producing output for turn {TurnId}.", turnId);
                throw;
            }

            ConversationMessage assistantEntity;
            if (terminalResourceChangeRequestId is { } requestId)
            {
                assistantEntity = await db.CoreConversationMessages.SingleOrDefaultAsync(x =>
                    x.ConversationId == conversation.Id &&
                    x.ChatTurnId == turnId &&
                    x.CorrelationId == requestId &&
                    x.SourceProvider == ResourceChangeService.MessageSource,
                    hardTimeout.Token) ?? throw new InvalidOperationException(
                    "The durable hiring-plan approval message could not be resolved for this turn.");
            }
            else
            {
                var assistant = await conversations.AppendMessageAsync(
                    conversation.Id, ConversationRole.Assistant, output.ToString(), hardTimeout.Token);
                assistantEntity = await db.CoreConversationMessages.SingleAsync(
                    x => x.Id == assistant.Id, hardTimeout.Token);
                assistantEntity.ChatTurnId = turnId;
                assistantEntity.SenderOrganizationUserId = turn.TargetAgentOrganizationUserId;
            }
            await QueueCommunicationReplyAsync(db, turn, userMessage, assistantEntity, hardTimeout.Token);
            await db.SaveChangesAsync(hardTimeout.Token);
            await AuditAssistantMessageAsync(
                db, audit, conversation, turn, assistantEntity, hardTimeout.Token);
            await turns.SetStatusAsync(turnId, ChatTurnStatus.FinalizingMemory.ToString(), cancellationToken: hardTimeout.Token);
            var memoryWarning = bypassMemory;
            if (bypassMemory)
            {
                await MarkMemoryCaptureBypassedAsync(db, assistantEntity.Id, "Direct provider fallback responses are excluded from memory.", hardTimeout.Token);
                await PublishTraceAsync(turns, turnId, "memory", "capture.bypassed", "warning", "Memory capture bypassed",
                    "This fallback response was intentionally excluded from memory.", new { reason = fallbackReason }, cancellationToken: hardTimeout.Token);
            }
            else
            {
                try
                {
                    using var memoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(hardTimeout.Token);
                    memoryTimeout.CancelAfter(options.Value.MemoryOperationTimeout);
                    await memory.CaptureMessageAsync(assistantEntity.Id, cancellationToken: memoryTimeout.Token);
                    await PublishTraceAsync(turns, turnId, "memory", "capture.completed", "completed", "Assistant episode captured",
                        "The assistant response was captured and durable enrichment was queued.", cancellationToken: hardTimeout.Token);
                }
                catch (OperationCanceledException) when (!hardTimeout.IsCancellationRequested)
                {
                    memoryWarning = true;
                    await PublishTraceAsync(turns, turnId, "memory", "capture.deferred", "warning", "Assistant capture deferred",
                        "Memory capture timed out and was queued for retry.", cancellationToken: hardTimeout.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    memoryWarning = true;
                    await PublishTraceAsync(turns, turnId, "memory", "capture.deferred", "warning", "Assistant capture deferred",
                        exception.Message, cancellationToken: hardTimeout.Token);
                }
            }

            await PublishTraceAsync(turns, turnId, "system", "turn.completed", memoryWarning ? "warning" : "completed",
                memoryWarning ? "Response completed with a memory warning" : "Turn completed",
                $"Completed in {Math.Max(1, (int)(DateTimeOffset.UtcNow - turn.CreatedAt).TotalSeconds)}s.", cancellationToken: hardTimeout.Token);
            await turns.CompleteAsync(turnId, assistantEntity.Id, memoryWarning, hardTimeout.Token);
            TurnCompletions.Add(1, new KeyValuePair<string, object?>("warning", memoryWarning));
            TurnDuration.Record((DateTimeOffset.UtcNow - turn.CreatedAt).TotalMilliseconds);
            if (turn.FirstOutputAt.HasValue) FirstOutputLatency.Record((turn.FirstOutputAt.Value - turn.CreatedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            await CompleteVisibleFailureAsync(services, turns, db, conversation, turnId, "timeout",
                $"I couldn't complete that request because it exceeded the {options.Value.HardTimeout.TotalMinutes:g}-minute safety limit. Please try again.", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Chat turn {TurnId} failed.", turnId);
            await CompleteVisibleFailureAsync(services, turns, db, conversation, turnId, "turn_failed",
                "The agent couldn't complete that request. Please try again.", CancellationToken.None);
        }
        finally
        {
            outputRouter.Complete(turnId);
            outputRouter.UnbindAlias(conversation.Id, turnId);
            eventRouter.Complete(turnId);
        }
    }

    internal static bool TryGetTerminalResourceChangeRequestId(
        ChatStreamChunk chunk,
        out Guid requestId)
    {
        requestId = Guid.Empty;
        return chunk.IsFinal &&
               string.Equals(chunk.Kind, "terminal-resource-change", StringComparison.Ordinal) &&
               chunk.Metadata is not null &&
               chunk.Metadata.TryGetValue("resourceChangeRequestId", out var value) &&
               Guid.TryParse(value, out requestId);
    }

    private async Task FailAsync(IChatTurnService turns, Guid turnId, string code, string message, CancellationToken cancellationToken)
    {
        await PublishTraceAsync(turns, turnId, "system", "turn.failed", "failed", "Turn failed", message,
            new { code }, cancellationToken: cancellationToken);
        await turns.SetStatusAsync(turnId, ChatTurnStatus.Failed.ToString(), code, message, cancellationToken);
        TurnFailures.Add(1, new KeyValuePair<string, object?>("code", code));
    }

    private async Task PumpAgentWorkAsync(
        Guid workId,
        Guid turnId,
        int attempt,
        CancellationToken cancellationToken)
    {
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<AgentWorkInbox>();
            var progress = await inbox.ReadProgressAfterAsync(workId, sequence, cancellationToken);
            foreach (var record in progress)
            {
                sequence = record.Sequence;
                var chunk = record.Value.Deserialize<AssistantResponseChunk>(JsonOptions);
                if (chunk is null)
                    continue;
                outputRouter.Publish(
                    turnId,
                    new ChatStreamChunk(
                        chunk.Sequence,
                        chunk.Delta,
                        chunk.IsFinal,
                        chunk.Error,
                        chunk.Kind,
                        chunk.Metadata,
                        chunk.Attempt == 0 ? attempt : chunk.Attempt));
            }

            var state = await inbox.ReadStateAsync(workId, cancellationToken);
            if (state.Status == AgentWorkStatus.Completed)
            {
                if (state.Completion is { Succeeded: false })
                    outputRouter.Publish(turnId, new ChatStreamChunk(
                        checked((int)sequence + 1),
                        state.Completion.Error ?? "Agent work failed.",
                        true,
                        "agent_work_failed",
                        "error",
                        Attempt: attempt));
                else if (progress.All(x =>
                             x.Value.Deserialize<AssistantResponseChunk>(JsonOptions)?.IsFinal != true))
                    outputRouter.Publish(turnId, new ChatStreamChunk(
                        checked((int)sequence + 1),
                        string.Empty,
                        true,
                        Attempt: attempt));
                return;
            }
            if (state.Status is AgentWorkStatus.Cancelled or AgentWorkStatus.DeadLetter)
            {
                outputRouter.Publish(turnId, new ChatStreamChunk(
                    checked((int)sequence + 1),
                    state.Error ?? "Agent work did not complete.",
                    true,
                    state.Status.ToString(),
                    "error",
                    Attempt: attempt));
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private async Task CompleteVisibleFailureAsync(
        IServiceProvider services,
        IChatTurnService turns,
        CSweetDbContext db,
        Conversation conversation,
        Guid turnId,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var current = await db.ChatTurns.SingleAsync(x => x.Id == turnId, cancellationToken);
        var separator = string.IsNullOrWhiteSpace(current.PartialResponse) ? string.Empty : "\n\n";
        var delta = separator + message;
        await turns.AppendOutputAsync(turnId, delta, cancellationToken);
        await PublishTraceAsync(turns, turnId, "output", "output.delta", "warning", "Assistant fallback message",
            delta, new { source = "deterministic_failure_fallback", memoryUsed = false, code }, cancellationToken: cancellationToken);

        var conversations = services.GetRequiredService<IConversationService>();
        var assistantContent = current.PartialResponse;
        var assistant = await conversations.AppendMessageAsync(conversation.Id, ConversationRole.Assistant, assistantContent, cancellationToken);
        var assistantEntity = await db.CoreConversationMessages.SingleAsync(x => x.Id == assistant.Id, cancellationToken);
        assistantEntity.ChatTurnId = turnId;
        assistantEntity.SenderOrganizationUserId = current.TargetAgentOrganizationUserId;
        await QueueCommunicationReplyAsync(db, current, current.UserMessage!, assistantEntity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AuditAssistantMessageAsync(
            db,
            services.GetRequiredService<IAuditEventWriter>(),
            conversation,
            current,
            assistantEntity,
            cancellationToken);
        await MarkMemoryCaptureBypassedAsync(db, assistant.Id, "Deterministic failure responses are excluded from memory.", cancellationToken);

        await PublishTraceAsync(turns, turnId, "memory", "capture.bypassed", "warning", "Memory capture bypassed",
            "The deterministic failure response was intentionally excluded from memory.", new { code }, cancellationToken: cancellationToken);
        await PublishTraceAsync(turns, turnId, "system", "turn.completed", "warning", "Turn completed with a fallback message",
            "The normal agent and memory-aware response path did not complete.", new { code }, cancellationToken: cancellationToken);
        await turns.CompleteAsync(turnId, assistant.Id, memoryWarning: true, cancellationToken);
        TurnFailures.Add(1, new KeyValuePair<string, object?>("code", code));
    }

    private static async Task MarkMemoryCaptureBypassedAsync(
        CSweetDbContext db,
        Guid messageId,
        string reason,
        CancellationToken cancellationToken)
    {
        var outbox = await db.MemoryCaptureOutbox.SingleOrDefaultAsync(
            x => x.ConversationMessageId == messageId,
            cancellationToken);
        if (outbox is null) return;
        var now = DateTimeOffset.UtcNow;
        outbox.Status = MemoryCaptureStatus.Completed;
        outbox.CompletedAt = now;
        outbox.NextAttemptAt = now;
        outbox.LastError = reason;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<CSweet.Contracts.Core.ChatTurnTraceEventResponse> PublishTraceAsync(
        IChatTurnService turns, Guid turnId, string category, string eventType, string status, string title,
        string? summary = null, object? details = null, string sensitivity = "Internal", long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        var traceEvent = await turns.TraceAsync(turnId, category, eventType, status, title, summary, details, sensitivity, durationMs, cancellationToken);
        eventRouter.Publish(traceEvent);
        return traceEvent;
    }

    private static Guid? GetConfiguredProviderId(AgentInstallationConfigurationSnapshot? configuration) =>
        configuration?.Settings.TryGetValue("llmProviderId", out var value) == true && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)
            ? id : null;

    private static string? GetConfiguredString(AgentInstallationConfigurationSnapshot? configuration, string key) =>
        configuration?.Settings.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static async Task QueueCommunicationReplyAsync(CSweetDbContext db, ChatTurn turn, ConversationMessage? userMessage,
        ConversationMessage assistantMessage, CancellationToken cancellationToken)
    {
        userMessage ??= await db.CoreConversationMessages.SingleAsync(x => x.Id == turn.UserMessageId, cancellationToken);
        if (string.IsNullOrWhiteSpace(userMessage.SourceProvider) ||
            string.Equals(userMessage.SourceProvider, "InApp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(userMessage.SourceChannelExternalId)) return;
        var providerKey = userMessage.SourceProvider.Trim().ToLowerInvariant();
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(x => x.OrganizationId == turn.OrganizationId &&
            x.ProviderKey == providerKey && x.Status != CommunicationConnectionStatus.Disconnected, cancellationToken);
        if (connection is null) return;
        var replyTo = await db.ExternalMessageReferences.Where(x => x.ConnectionId == connection.Id && x.ConversationMessageId == userMessage.Id)
            .Select(x => x.MessageExternalId).SingleOrDefaultAsync(cancellationToken);
        var persona = await db.CoreOrganizationUsers.Where(x => x.Id == turn.TargetAgentOrganizationUserId)
            .Select(x => x.DisplayName).SingleAsync(cancellationToken);
        var envelope = new OutboundCommunicationEnvelope(Guid.NewGuid(), connection.ProviderKey, connection.WorkspaceExternalId,
            userMessage.SourceChannelExternalId, assistantMessage.Content, null, replyTo, persona, null,
            $"communication-reply:{connection.ProviderKey}:{assistantMessage.Id:D}");
        var now = DateTimeOffset.UtcNow;
        db.CommunicationDeliveries.Add(new CommunicationDelivery
        {
            Id = Guid.NewGuid(), OrganizationId = turn.OrganizationId, ConnectionId = connection.Id,
            OrganizationUserId = turn.TargetAgentOrganizationUserId, ConversationMessageId = assistantMessage.Id,
            Kind = CommunicationDeliveryKind.SendMessage, Status = CommunicationDeliveryStatus.Pending,
            IdempotencyKey = envelope.IdempotencyKey, PayloadJson = JsonSerializer.Serialize(envelope),
            NextAttemptAt = now, CreatedAt = now, UpdatedAt = now
        });
    }

    private static async Task AuditAssistantMessageAsync(
        CSweetDbContext db,
        IAuditEventWriter audit,
        Conversation conversation,
        ChatTurn turn,
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking()
            .SingleAsync(x => x.Id == turn.TargetAgentOrganizationUserId, cancellationToken);
        var recipients = await (
            from participant in db.ConversationParticipants.AsNoTracking()
            join user in db.CoreOrganizationUsers.AsNoTracking()
                on participant.OrganizationUserId equals user.Id
            where participant.ConversationId == conversation.Id &&
                  participant.LeftAt == null &&
                  user.Id != actor.Id
            orderby user.DisplayName
            select new
            {
                user.Id,
                user.DisplayName,
                EmployeeType = user.EmployeeType.ToString(),
                user.AgentInstallationId
            }).ToListAsync(cancellationToken);
        var directRecipient = recipients.Count == 1 ? recipients[0] : null;
        var targetName = directRecipient?.DisplayName ??
            (!string.IsNullOrWhiteSpace(conversation.Title)
                ? $"#{conversation.Title}"
                : $"{recipients.Count} recipients");
        var contentBytes = Encoding.UTF8.GetBytes(message.Content);
        await audit.AppendAsync(new AuditEventWriteRequest(
            "communication.message.sent",
            "Communication",
            "Outbound",
            "Delivered",
            conversation.OrganizationId,
            "ConversationMessage",
            message.Id,
            $"{actor.DisplayName} sent a message to {targetName}.",
            JsonSerializer.Serialize(new
            {
                chatId = conversation.Id,
                chatKind = conversation.Kind.ToString(),
                chatTurnId = turn.Id,
                recipients,
                contentBytes = contentBytes.Length,
                contentSha256 = Convert.ToHexString(SHA256.HashData(contentBytes))
            }, JsonOptions),
            ExternalMessageId: message.Id.ToString("D"),
            CorrelationId: message.CorrelationId.ToString("D"),
            Actor: new AuditActor(
                "Agent",
                true,
                OrganizationUserId: actor.Id,
                DisplayName: actor.DisplayName,
                AgentId: actor.DisplayName,
                InstallationId: actor.AgentInstallationId),
            Target: new AuditTarget(
                directRecipient?.EmployeeType ?? "Conversation",
                targetName,
                directRecipient?.EmployeeType == EmployeeType.Agent.ToString()
                    ? directRecipient.DisplayName
                    : null,
                directRecipient?.AgentInstallationId),
            ContentType: "text/plain"),
            cancellationToken);
    }

}
