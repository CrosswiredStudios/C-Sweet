using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

public static class McpGatewayEndpoints
{
    private const string ProtocolVersion = "2025-06-18";
    private const int MaximumRequestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCSweetMcpGateway(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/mcp", HandlePostAsync)
            .RequireRateLimiting("mcp-session")
            .DisableAntiforgery();
        endpoints.MapGet("/mcp", () => Results.NotFound());
        endpoints.MapDelete("/mcp", () => Results.NoContent());
        return endpoints;
    }

    private static async Task<IResult> HandlePostAsync(
        HttpContext http,
        McpAgentSessionService sessions,
        AgentWorkInbox inbox,
        McpToolCatalog catalog,
        CSweetDbContext db,
        IPlatformCapabilityDispatcher dispatcher,
        IAgentRuntimeSignalService runtimeSignals,
        IAuditEventWriter audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (http.Request.ContentLength is > MaximumRequestBytes)
            return RpcError(null, -32600, "The MCP request exceeds 1 MiB.", StatusCodes.Status413PayloadTooLarge);
        var body = await ReadLimitedBodyAsync(http.Request.Body, cancellationToken);
        if (body is null)
            return RpcError(null, -32600, "The MCP request exceeds 1 MiB.", StatusCodes.Status413PayloadTooLarge);

        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            return RpcError(null, -32700, "Invalid JSON.", StatusCodes.Status400BadRequest);
        }

        using (document)
        {
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : (JsonElement?)null;
            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
                return RpcError(id, -32600, "A JSON-RPC method is required.", StatusCodes.Status400BadRequest);
            var method = methodElement.GetString()!;
            try
            {
                if (method == "initialize")
                    return await InitializeAsync(id, root, http, sessions, audit, cancellationToken);

                var token = ReadBearerToken(http.Request.Headers.Authorization);
                var session = token is null
                    ? null
                    : await sessions.AuthenticateAsync(
                        token,
                        http.Request.Headers["Mcp-Session-Id"].FirstOrDefault(),
                        cancellationToken);
                if (session is null)
                    return RpcError(id, -32001, "The MCP session is invalid, expired, or revoked.", StatusCodes.Status401Unauthorized);

                http.Response.Headers["Mcp-Session-Id"] = session.SessionId;
                var result = method switch
                {
                    "ping" => Results.Json(Success(id, new { })),
                    "tools/list" => await ListToolsAsync(id, session, catalog, db, cancellationToken),
                    "tools/call" => await CallToolAsync(
                        id,
                        root,
                        session,
                        catalog,
                        db,
                        dispatcher,
                        inbox,
                        audit,
                        loggerFactory.CreateLogger("CSweet.AgentHost.Broker.McpGateway"),
                        cancellationToken),
                    "csweet/session/renew" => await RenewAsync(id, session, sessions, http, cancellationToken),
                    "csweet/work/claim" => await ClaimAsync(id, root, session, inbox, audit, cancellationToken),
                    "csweet/work/renew" => await RenewWorkAsync(id, root, session, inbox, cancellationToken),
                    "csweet/work/progress" => await ProgressAsync(id, root, session, inbox, cancellationToken),
                    "csweet/work/complete" => await CompleteWorkAsync(id, root, session, inbox, audit, cancellationToken),
                    "csweet/work/fail" => await FailWorkAsync(id, root, session, inbox, audit, cancellationToken),
                    "csweet/runtime/complete" => await CompleteRuntimeAsync(
                        id, root, session, runtimeSignals, cancellationToken),
                    _ => RpcError(id, -32601, $"Method '{method}' is not supported.", StatusCodes.Status404NotFound)
                };
                if (method is "csweet/session/renew" or "csweet/runtime/complete")
                    await WriteAuditAsync(audit, session, method, "Completed", null, cancellationToken);
                return result;
            }
            catch (UnauthorizedAccessException exception)
            {
                return RpcError(id, -32003, exception.Message, StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException exception)
            {
                return RpcError(id, -32602, exception.Message, StatusCodes.Status400BadRequest);
            }
            catch (JsonException)
            {
                return RpcError(id, -32602, "The MCP parameters are invalid.", StatusCodes.Status400BadRequest);
            }
        }
    }

    private static async Task<IResult> InitializeAsync(
        JsonElement? id,
        JsonElement root,
        HttpContext http,
        McpAgentSessionService sessions,
        IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        var workloadToken = ReadBearerToken(http.Request.Headers.Authorization)
            ?? throw new UnauthorizedAccessException("A workload token is required.");
        var parameters = RequiredParameters(root);
        var client = parameters.GetProperty("clientInfo");
        var metadata = parameters.GetProperty("_meta").GetProperty("csweet");
        var issue = await sessions.EstablishAsync(
            workloadToken,
            metadata.GetProperty("runtimeInstanceId").GetGuid(),
            metadata.GetProperty("tickId").GetGuid(),
            metadata.GetProperty("installationId").GetGuid(),
            metadata.GetProperty("businessId").GetString()!,
            metadata.GetProperty("agentId").GetString()!,
            client.GetProperty("version").GetString()!,
            cancellationToken);
        http.Response.Headers["Mcp-Session-Id"] = issue.Session.SessionId;
        await WriteAuditAsync(audit, issue.Session, "initialize", "Established", null, cancellationToken);
        return Results.Json(Success(id, new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { tools = new { listChanged = true } },
            serverInfo = new { name = "csweet-agent-runtime", version = "2.0.0" },
            _meta = new
            {
                csweet = new
                {
                    sessionId = issue.Session.SessionId,
                    accessToken = issue.AccessToken,
                    expiresAt = issue.ExpiresAt,
                    grantRevision = issue.Session.Grant.Revision,
                    identity = issue.Identity,
                    configuration = issue.Configuration
                }
            }
        }));
    }

    private static async Task<IResult> ListToolsAsync(
        JsonElement? id,
        AgentSession session,
        McpToolCatalog catalog,
        CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var tools = (await catalog.ListAsync(session, db, cancellationToken)).Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = tool.InputSchema,
            outputSchema = tool.OutputSchema,
            annotations = new
            {
                readOnlyHint = tool.ExecutionPolicy == McpToolExecutionPolicy.ReadOnly,
                destructiveHint = false,
                idempotentHint = tool.ExecutionPolicy != McpToolExecutionPolicy.ApprovalCreating,
                openWorldHint = tool.Name.Contains("search", StringComparison.Ordinal)
            },
            _meta = new
            {
                csweet = new
                {
                    capability = tool.Capability,
                    riskClass = tool.RiskClass,
                    scopeResolver = tool.ScopeResolver,
                    maximumInputBytes = tool.MaximumInputBytes,
                    maximumOutputBytes = tool.MaximumOutputBytes,
                    quotaClass = tool.QuotaClass,
                    approvalBehavior = tool.ApprovalBehavior,
                    owningService = tool.OwningService,
                    grantRevision = session.Grant.Revision,
                    modelVisible = tool.ModelVisible
                }
            }
        });
        return Results.Json(Success(id, new
        {
            tools,
            _meta = new { csweet = new { grantRevision = session.Grant.Revision } }
        }));
    }

    private static async Task<IResult> CallToolAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        McpToolCatalog catalog,
        CSweetDbContext db,
        IPlatformCapabilityDispatcher dispatcher,
        AgentWorkInbox inbox,
        IAuditEventWriter audit,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var parameters = RequiredParameters(root);
        var name = parameters.GetProperty("name").GetString()
            ?? throw new JsonException();
        var tool = await catalog.FindAsync(name, session, db, cancellationToken)
            ?? throw new UnauthorizedAccessException("The tool is not in the current installation grant.");
        var arguments = parameters.TryGetProperty("arguments", out var value)
            ? value
            : JsonDocument.Parse("{}").RootElement.Clone();
        JsonSchemaValidator.Validate(arguments, tool.InputSchema);

        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = tool.Capability,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(arguments, JsonOptions))
        };
        CapabilityResult? terminal;
        if (tool.ProviderInstallationId is { } providerInstallationId)
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(tool.ExecutionTimeoutSeconds, 1, 900));
            var callerKey = arguments.ValueKind == JsonValueKind.Object &&
                            arguments.TryGetProperty("idempotencyKey", out var idempotency) &&
                            idempotency.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(idempotency.GetString())
                ? idempotency.GetString()!
                : request.RequestId;
            var work = await inbox.EnqueueAsync(
                session.BusinessId,
                providerInstallationId,
                AgentWorkKind.Capability,
                tool.Capability,
                arguments,
                $"mcp-call:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{session.InstallationId}:{tool.Capability}:{callerKey}")))}",
                DateTimeOffset.UtcNow.Add(timeout),
                request.RequestId,
                sourceType: "AgentInstallation",
                sourceId: session.InstallationId,
                cancellationToken: cancellationToken);
            var providerValue = await inbox.WaitForResultAsync<JsonElement>(
                work.Id,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
            terminal = new CapabilityResult
            {
                RequestId = request.RequestId,
                Succeeded = true,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(providerValue, JsonOptions))
            };
        }
        else
        {
            terminal = null;
            await foreach (var result in dispatcher.InvokeAsync(session, request, cancellationToken))
                terminal = result;
        }
        if (terminal is null)
            throw new InvalidOperationException("The platform capability returned no result.");

        if (!terminal.Succeeded)
        {
            logger.LogWarning(
                "Platform capability {Capability} ({ToolName}) failed for agent {AgentId}, installation {InstallationId}, request {RequestId}: {Error}",
                tool.Capability,
                tool.Name,
                session.AgentId,
                session.InstallationId,
                request.RequestId,
                string.IsNullOrWhiteSpace(terminal.Error)
                    ? "No failure reason was supplied by the capability handler."
                    : terminal.Error);
        }

        JsonNode? structured = null;
        if (!terminal.Payload.IsEmpty)
        {
            structured = JsonNode.Parse(terminal.Payload.Span);
            if (tool.OutputSchema is { } outputSchema && structured is not null)
                JsonSchemaValidator.Validate(
                    JsonSerializer.SerializeToElement(structured, JsonOptions),
                    outputSchema);
        }
        await WriteCapabilityAuditAsync(
            audit, session, tool, request, terminal, cancellationToken);
        return Results.Json(Success(id, new
        {
            content = new[] { new { type = "text", text = GetToolResponseText(terminal) } },
            structuredContent = structured,
            isError = !terminal.Succeeded
        }));
    }

    internal static string GetToolResponseText(CapabilityResult result)
    {
        if (!result.Payload.IsEmpty)
            return result.Payload.ToStringUtf8();
        if (!string.IsNullOrWhiteSpace(result.Error))
            return result.Error;
        return result.Succeeded
            ? string.Empty
            : "The platform capability failed without an error message.";
    }

    private static async Task<IResult> RenewAsync(
        JsonElement? id,
        AgentSession session,
        McpAgentSessionService sessions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var issue = await sessions.RenewAsync(session, cancellationToken);
        http.Response.Headers["Mcp-Session-Id"] = issue.Session.SessionId;
        return Results.Json(Success(id, new
        {
            _meta = new
            {
                csweet = new
                {
                    accessToken = issue.AccessToken,
                    expiresAt = issue.ExpiresAt,
                    grantRevision = issue.Session.Grant.Revision
                }
            }
        }));
    }

    private static async Task<IResult> ClaimAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        AgentWorkInbox inbox,
        IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        var parameters = RequiredParameters(root);
        var waitSeconds = parameters.TryGetProperty("waitSeconds", out var wait)
            ? Math.Clamp(wait.GetInt32(), 0, 25)
            : 25;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        ClaimedAgentWork? work;
        do
        {
            work = await inbox.ClaimAsync(ToPersistedSession(session), cancellationToken);
            if (work is not null || waitSeconds == 0)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);

        if (work is not null)
        {
            await audit.AppendAsync(new AuditEventWriteRequest(
                "agent.work.claimed",
                "AgentWork",
                "Internal",
                "Accepted",
                RuntimeAuditIdentity.OrganizationId(session),
                "AgentWorkItem",
                work.WorkId,
                $"{session.AgentId} claimed {work.Kind.ToString().ToLowerInvariant()} work '{work.Name}'.",
                JsonSerializer.Serialize(new
                {
                    work.Attempt,
                    kind = work.Kind.ToString(),
                    work.Name,
                    work.LeaseExpiresAt,
                    work.Deadline
                }, JsonOptions),
                ExternalRequestId: id?.ToString(),
                CorrelationId: work.CorrelationId,
                Actor: RuntimeAuditIdentity.Actor(session)),
                cancellationToken);
        }

        return Results.Json(Success(id, new
        {
            work = work is null ? null : new
            {
                workId = work.WorkId,
                attempt = work.Attempt,
                kind = work.Kind.ToString(),
                name = work.Name,
                payload = work.Payload,
                leaseToken = work.LeaseToken,
                leaseExpiresAt = work.LeaseExpiresAt,
                deadline = work.Deadline,
                eventId = work.EventId,
                correlationId = work.CorrelationId
            }
        }));
    }

    private static async Task<IResult> RenewWorkAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        AgentWorkInbox inbox,
        CancellationToken cancellationToken)
    {
        var p = RequiredParameters(root);
        var expiry = await inbox.RenewAsync(
            ToPersistedSession(session),
            p.GetProperty("workId").GetGuid(),
            p.GetProperty("attempt").GetInt32(),
            p.GetProperty("leaseToken").GetString()!,
            cancellationToken);
        return Results.Json(Success(id, new { leaseExpiresAt = expiry }));
    }

    private static async Task<IResult> ProgressAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        AgentWorkInbox inbox,
        CancellationToken cancellationToken)
    {
        var p = RequiredParameters(root);
        await inbox.AppendProgressAsync(
            ToPersistedSession(session),
            p.GetProperty("workId").GetGuid(),
            p.GetProperty("attempt").GetInt32(),
            p.GetProperty("leaseToken").GetString()!,
            p.GetProperty("sequence").GetInt64(),
            p.GetProperty("value").Clone(),
            cancellationToken);
        return Results.Json(Success(id, new { accepted = true }));
    }

    private static async Task<IResult> CompleteWorkAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        AgentWorkInbox inbox,
        IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        var p = RequiredParameters(root);
        var result = p.GetProperty("result");
        var workId = p.GetProperty("workId").GetGuid();
        var attempt = p.GetProperty("attempt").GetInt32();
        var succeeded = result.GetProperty("succeeded").GetBoolean();
        var errorMessage = result.TryGetProperty("error", out var error) ? error.GetString() : null;
        await inbox.CompleteAsync(
            ToPersistedSession(session),
            workId,
            attempt,
            p.GetProperty("leaseToken").GetString()!,
            new AgentWorkCompletion(
                succeeded,
                result.TryGetProperty("value", out var value) ? value.Clone() : null,
                errorMessage),
            cancellationToken);
        await audit.AppendAsync(new AuditEventWriteRequest(
            succeeded ? "agent.work.completed" : "agent.work.failed",
            "AgentWork",
            "Internal",
            succeeded ? "Completed" : "Failed",
            RuntimeAuditIdentity.OrganizationId(session),
            "AgentWorkItem",
            workId,
            $"{session.AgentId} completed work attempt {attempt} {(succeeded ? "successfully" : "with an error")}.",
            JsonSerializer.Serialize(new { attempt }, JsonOptions),
            ExternalRequestId: id?.ToString(),
            Actor: RuntimeAuditIdentity.Actor(session),
            ErrorCode: succeeded ? null : "agent_reported_failure",
            ErrorMessage: errorMessage),
            cancellationToken);
        return Results.Json(Success(id, new { accepted = true }));
    }

    private static async Task<IResult> FailWorkAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        AgentWorkInbox inbox,
        IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        var p = RequiredParameters(root);
        var workId = p.GetProperty("workId").GetGuid();
        var attempt = p.GetProperty("attempt").GetInt32();
        var errorMessage = p.GetProperty("error").GetString() ?? "Agent work failed.";
        await inbox.FailAsync(
            ToPersistedSession(session),
            workId,
            attempt,
            p.GetProperty("leaseToken").GetString()!,
            errorMessage,
            cancellationToken);
        await audit.AppendAsync(new AuditEventWriteRequest(
            "agent.work.failed",
            "AgentWork",
            "Internal",
            "Failed",
            RuntimeAuditIdentity.OrganizationId(session),
            "AgentWorkItem",
            workId,
            $"{session.AgentId} failed work attempt {attempt}.",
            JsonSerializer.Serialize(new { attempt }, JsonOptions),
            ExternalRequestId: id?.ToString(),
            Actor: RuntimeAuditIdentity.Actor(session),
            ErrorCode: "agent_work_failed",
            ErrorMessage: errorMessage),
            cancellationToken);
        return Results.Json(Success(id, new { accepted = true }));
    }

    private static async Task<IResult> CompleteRuntimeAsync(
        JsonElement? id,
        JsonElement root,
        AgentSession session,
        IAgentRuntimeSignalService runtimeSignals,
        CancellationToken cancellationToken)
    {
        var payload = RequiredParameters(root).GetRawText();
        await runtimeSignals.RecordCompletionAsync(
            Guid.Parse(session.RuntimeInstanceId),
            Guid.Parse(session.TickId),
            Guid.Parse(session.InstallationId),
            payload,
            cancellationToken);
        return Results.Json(Success(id, new { accepted = true }));
    }

    private static CSweet.Domain.Setup.McpAgentSession ToPersistedSession(AgentSession session) => new()
    {
        Id = Guid.Parse(session.SessionId),
        RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId),
        TickId = Guid.Parse(session.TickId),
        AgentInstallationId = Guid.Parse(session.InstallationId),
        OrganizationId = session.BusinessId,
        GrantRevision = session.Grant.Revision
    };

    private static JsonElement RequiredParameters(JsonElement root) =>
        root.TryGetProperty("params", out var parameters) &&
        parameters.ValueKind == JsonValueKind.Object
            ? parameters
            : throw new JsonException();

    private static async Task<byte[]?> ReadLimitedBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumRequestBytes)
                return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static string? ReadBearerToken(string? authorization)
    {
        const string prefix = "Bearer ";
        return authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    private static object Success(JsonElement? id, object result) =>
        new { jsonrpc = "2.0", id, result };

    private static IResult RpcError(JsonElement? id, int code, string message, int status) =>
        Results.Json(new { jsonrpc = "2.0", id, error = new { code, message } }, statusCode: status);

    private static Task<Guid> WriteAuditAsync(
        IAuditEventWriter audit,
        AgentSession session,
        string operation,
        string outcome,
        string? error,
        CancellationToken cancellationToken)
    {
        var (eventType, category, summary) = operation switch
        {
            "initialize" => ("mcp.session.established", "Mcp", $"{session.AgentId} established an MCP runtime session."),
            "csweet/session/renew" => ("mcp.session.renewed", "Mcp", $"{session.AgentId} renewed its MCP runtime session."),
            "csweet/runtime/complete" => ("agent.runtime.completed", "AgentRuntime", $"{session.AgentId} reported runtime completion."),
            _ => ("mcp.runtime.operation", "Mcp", $"{session.AgentId} completed {operation}.")
        };
        return audit.AppendAsync(new AuditEventWriteRequest(
            eventType,
            category,
            "Internal",
            outcome,
            RuntimeAuditIdentity.OrganizationId(session),
            "McpMethod",
            Summary: summary,
            Actor: RuntimeAuditIdentity.Actor(session),
            ErrorCode: error is null ? null : "operation_failed",
            ErrorMessage: error),
            cancellationToken);
    }

    private static Task<Guid> WriteCapabilityAuditAsync(
        IAuditEventWriter audit,
        AgentSession session,
        McpToolDescriptor tool,
        RequestCapability request,
        CapabilityResult result,
        CancellationToken cancellationToken)
    {
        var input = request.Payload.Span;
        var output = result.Payload.Span;
        return audit.AppendAsync(new AuditEventWriteRequest(
            "agent.capability.executed",
            "AgentCapability",
            "Internal",
            result.Succeeded ? "Completed" : "Failed",
            RuntimeAuditIdentity.OrganizationId(session),
            "Capability",
            Summary: $"{session.AgentId} invoked {tool.Name} ({tool.Capability}).",
            MetadataJson: JsonSerializer.Serialize(new
            {
                toolName = tool.Name,
                tool.Capability,
                executionPolicy = tool.ExecutionPolicy.ToString(),
                tool.RiskClass,
                tool.ApprovalBehavior,
                tool.OwningService,
                inputBytes = input.Length,
                inputSha256 = Convert.ToHexString(SHA256.HashData(input)),
                outputBytes = output.Length,
                outputSha256 = Convert.ToHexString(SHA256.HashData(output))
            }, JsonOptions),
            ExternalRequestId: request.RequestId,
            CorrelationId: request.RequestId,
            Actor: RuntimeAuditIdentity.Actor(session),
            Target: new AuditTarget(
                tool.ProviderInstallationId.HasValue ? "AgentInstallation" : "PlatformService",
                tool.OwningService,
                InstallationId: tool.ProviderInstallationId),
            ErrorCode: result.Succeeded ? null : "capability_failed",
            ErrorMessage: result.Error),
            cancellationToken);
    }
}
