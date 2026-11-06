using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Limits for the codemode capability. Codemode is <b>off</b> unless a deployment opts in, because it
/// hands an agent a small interpreter loop inside the broker; every other limit here is a bound on
/// that loop rather than a tuning knob.
/// </summary>
public sealed class CodemodeOptions
{
    public const string SectionName = "CSweet:Codemode";

    /// <summary>False by default: an installation must be explicitly switched on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Largest accepted script, in UTF-8 bytes.</summary>
    public int MaximumScriptBytes { get; set; } = 16384;

    /// <summary>Largest number of statements in one script.</summary>
    public int MaximumStatements { get; set; } = 48;

    /// <summary>Largest number of capability calls one script may make.</summary>
    public int MaximumCapabilityCalls { get; set; } = 24;

    /// <summary>Wall-clock budget used when the request does not ask for one.</summary>
    public int DefaultTimeoutMs { get; set; } = 5000;

    /// <summary>Largest wall-clock budget a request may ask for.</summary>
    public int MaximumTimeoutMs { get; set; } = 30000;

    /// <summary>Largest output returned to the agent, in UTF-8 bytes.</summary>
    public int MaximumOutputBytes { get; set; } = 32768;

    /// <summary>Reads the section and forces every limit into a range the interpreter can defend.</summary>
    public static CodemodeOptions From(IConfiguration? configuration)
    {
        var options = configuration?.GetSection(SectionName).Get<CodemodeOptions>() ?? new CodemodeOptions();
        options.MaximumScriptBytes = Math.Clamp(options.MaximumScriptBytes, 1024, 262144);
        options.MaximumStatements = Math.Clamp(options.MaximumStatements, 1, 256);
        options.MaximumCapabilityCalls = Math.Clamp(options.MaximumCapabilityCalls, 1, 64);
        options.DefaultTimeoutMs = Math.Clamp(options.DefaultTimeoutMs, 100, 60000);
        options.MaximumTimeoutMs = Math.Clamp(options.MaximumTimeoutMs, options.DefaultTimeoutMs, 120000);
        options.MaximumOutputBytes = Math.Clamp(options.MaximumOutputBytes, 1024, 1048576);
        return options;
    }
}

/// <summary>
/// The single door a script has for reaching another capability. The default implementation hands the
/// call to <see cref="IPlatformCapabilityDispatcher"/>, so a script call is re-authorised exactly like a
/// direct broker call (grant, setup policy, project policy, retention) instead of inheriting the
/// script's authority.
/// </summary>
internal interface ICodemodeCapabilityGateway
{
    IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the dispatcher at call time rather than construction time. The dispatcher itself is built
/// from every <see cref="IPlatformCapabilityHandler"/>, so taking it as a constructor dependency here
/// would close a DI cycle; resolving lazily inside an already-running scope hands back the very
/// instance that is executing this call.
/// </summary>
internal sealed class DispatcherCodemodeCapabilityGateway(IServiceProvider services) : ICodemodeCapabilityGateway
{
    public IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<IPlatformCapabilityDispatcher>().InvokeAsync(session, request, cancellationToken);
}

/// <summary>
/// Runs one short, read-only script over capabilities the installation has already been granted, and
/// returns only the value the script emits.
/// </summary>
/// <remarks>
/// <para>
/// The point is round trips, not power: an agent that needs eight reads today pays eight MCP turns of
/// latency and eight copies of intermediate payload in its context window. One script pays one.
/// </para>
/// <para>
/// Safety comes from the surface, not from a sandbox runtime. The language has no I/O, no eval, no
/// imports, no loops, no recursion and no reference to anything in the CLR; every side effect a script
/// can cause has to be spelled as a capability name, and only names classified
/// <see cref="CapabilityRiskClass.Read"/> that the installation was granted are accepted. Network and
/// filesystem access are denied twice over: the interpreter has no primitive for either, and the
/// known egress capabilities are refused by name and by prefix before their risk class is even
/// consulted.
/// </para>
/// </remarks>
public sealed class CodemodeCapabilityHandler : IPlatformCapabilityHandler
{
    private const int MaximumManifestCapabilities = 256;
    private const int MaximumNestedErrorCharacters = 240;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Read-only capabilities that a script still may not call. They are read-shaped but they leave the
    /// instance (egress), cost money per call, or both. Names are matched case-insensitively.
    /// </summary>
    private static readonly string[] DeniedCapabilities =
    [
        "platform.llm.chat-stream.v1",
        "platform.llm.chat.v1",
        "web.fetch.v1",
        "web.request.v1",
        "web.socket.v1",
        "web.research.read.v1",
        "web.research.search.v1",
        "genai.image.generate.v1",
        "genai.image.edit.v1",
        "genai.video.generate.v1",
        "genai.video.edit.v1",
        "git.workspace.publish.v2",
        "platform.connector.action.request.v1",
        "network.publish-port.v1"
    ];

    /// <summary>
    /// Prefixes refused regardless of classification, so a capability the catalog has not learned about
    /// yet cannot be reached by naming it in a script. Codemode itself is included: scripts do not nest.
    /// </summary>
    private static readonly string[] DeniedCapabilityPrefixes =
    [
        "web.",
        "network.",
        "platform.web",
        "platform.codemode.",
        "file.",
        "filesystem.",
        "fs.",
        "shell.",
        "exec.",
        "process.",
        "host."
    ];

    private static readonly CodemodeStatementSpec[] Statements =
    [
        new("call", "call <capability> <json-expression>",
            "Invoke one read-only capability. The result is bound to $<n> (n = call index, from 0) and to $_ (last result)."),
        new("let", "let <name> = <expression>",
            "Bind a name so later statements can reference it as $<name>."),
        new("emit", "emit <expression>",
            "Set the value returned to you. Only the last emit counts. With no emit, the last call result is returned.")
    ];

    private static readonly IReadOnlyList<string> AllowedRiskClasses = [nameof(CapabilityRiskClass.Read)];

    private readonly ICodemodeCapabilityGateway _gateway;
    private readonly CodemodeOptions _options;

    public CodemodeCapabilityHandler(IServiceProvider services, IConfiguration configuration)
        : this(new DispatcherCodemodeCapabilityGateway(services), configuration)
    {
    }

    internal CodemodeCapabilityHandler(ICodemodeCapabilityGateway gateway, IConfiguration? configuration)
    {
        _gateway = gateway;
        _options = CodemodeOptions.From(configuration);
    }

    public bool CanHandle(string capability) => CodemodeCapabilities.All.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await ExecuteAsync(session, request, cancellationToken);
    }

    /// <summary>
    /// One entry point for both capabilities. Nothing thrown inside ever reaches the caller: a script
    /// failure is data, and an unexpected failure is reported as a generic structured refusal.
    /// </summary>
    private async Task<CapabilityResult> ExecuteAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (!CodemodeCapabilities.All.Contains(request.Capability))
                return Failure(request.RequestId, CodemodeFailureCodes.Denied,
                    "Codemode is not granted to this installation.", start);

            var granted = session.Grant.RequestedCapabilities;
            if (granted is null || !granted.Contains(request.Capability))
                return Failure(request.RequestId, CodemodeFailureCodes.Denied,
                    "Codemode is not granted to this installation.", start);

            if (request.Capability == CodemodeCapabilities.Manifest)
            {
                var manifest = BuildManifest(granted);
                return Success(request.RequestId, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            }

            var payload = request.Payload;
            if (payload.Length > _options.MaximumScriptBytes * 2 + 4096)
                return Failure(request.RequestId, CodemodeFailureCodes.ScriptInvalid,
                    "The codemode request is larger than this deployment accepts.", start);

            CodemodeExecuteRequest? input;
            try
            {
                input = JsonSerializer.Deserialize<CodemodeExecuteRequest>(payload.Span, Json);
            }
            catch (JsonException)
            {
                return Failure(request.RequestId, CodemodeFailureCodes.ScriptInvalid,
                    "The codemode request is not a valid codemode object.", start);
            }

            if (input is null || string.IsNullOrWhiteSpace(input.Script))
                return Failure(request.RequestId, CodemodeFailureCodes.ScriptInvalid,
                    "A script is required.", start);

            var result = await RunScriptAsync(session, granted, input, request.RequestId, start, cancellationToken);
            return result.Succeeded
                ? Success(request.RequestId, JsonSerializer.SerializeToUtf8Bytes(result, Json))
                : FailureResult(request.RequestId, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(request.RequestId, CodemodeFailureCodes.Internal,
                "Codemode could not complete the request.", start);
        }
    }

    private CodemodeManifest BuildManifest(IReadOnlySet<string> granted)
    {
        var allowed = granted
            .Where(IsReachableReadCapability)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(MaximumManifestCapabilities)
            .ToArray();
        return new CodemodeManifest
        {
            Capability = CodemodeCapabilities.Manifest,
            Enabled = _options.Enabled,
            Language = CodemodeLanguage,
            Statements = Statements,
            AllowedCapabilities = allowed,
            DeniedCapabilities = DeniedCapabilities,
            DeniedCapabilityPrefixes = DeniedCapabilityPrefixes,
            AllowedRiskClasses = AllowedRiskClasses,
            MaximumScriptBytes = _options.MaximumScriptBytes,
            MaximumStatements = _options.MaximumStatements,
            MaximumCapabilityCalls = _options.MaximumCapabilityCalls,
            DefaultTimeoutMs = _options.DefaultTimeoutMs,
            MaximumTimeoutMs = _options.MaximumTimeoutMs,
            MaximumOutputBytes = _options.MaximumOutputBytes,
            FilesystemAccess = false,
            NetworkAccess = false,
            NestedScripts = false
        };
    }

    /// <summary>
    /// The whole read-only gate, in one place: a name is reachable from a script only when the catalog
    /// classifies it <see cref="CapabilityRiskClass.Read"/> (unknown names default to Write, so they are
    /// refused) and it is not on either deny list.
    /// </summary>
    private static bool IsReachableReadCapability(string capability) =>
        CapabilityRiskCatalog.Classify(capability) == CapabilityRiskClass.Read && !IsDenied(capability);

    private static bool IsDenied(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return true;
        // One source for "read-shaped but still egress" so this list cannot drift from the catalog.
        if (CapabilityRiskCatalog.EgressReadCapabilities.Contains(capability))
            return true;
        foreach (var denied in DeniedCapabilities)
        {
            if (string.Equals(denied, capability, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var prefix in DeniedCapabilityPrefixes)
        {
            if (capability.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static CapabilityResult Success(string requestId, byte[] payload) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        ContentType = "application/json",
        Payload = JsonPayload.From(payload)
    };

    private static CapabilityResult Failure(string requestId, string code, string message, long start) =>
        FailureResult(requestId,
            CodemodeExecuteResult.Failed(new CodemodeFailure(code, message), Elapsed(start)));

    private static CapabilityResult FailureResult(string requestId, CodemodeExecuteResult result) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = result.Error?.Message ?? "Codemode could not complete the request.",
        FailureCode = result.Error?.Code,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(result, Json))
    };

    private static long Elapsed(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private const string CodemodeLanguage = "codemode/1";

    /// <summary>
    /// The whole execution path for one script: parse, then run under a linked deadline, then render.
    /// Every early exit is a <see cref="CodemodeExecuteResult"/>, never an exception, because a script
    /// author needs to read the reason and retry.
    /// </summary>
    private async Task<CodemodeExecuteResult> RunScriptAsync(
        AgentSession session,
        IReadOnlySet<string> granted,
        CodemodeExecuteRequest input,
        string requestId,
        long start,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return CodemodeExecuteResult.Failed(new CodemodeFailure(CodemodeFailureCodes.Disabled,
                "Codemode is disabled for this deployment."), Elapsed(start));

        if (Encoding.UTF8.GetByteCount(input.Script) > _options.MaximumScriptBytes)
            return CodemodeExecuteResult.Failed(new CodemodeFailure(CodemodeFailureCodes.ScriptInvalid,
                $"The script exceeds the {_options.MaximumScriptBytes}-byte limit."), Elapsed(start));

        HashSet<string>? allowList = null;
        if (input.AllowedCapabilities is { Count: > 0 } requested)
            allowList = new HashSet<string>(
                requested.Take(MaximumManifestCapabilities), StringComparer.Ordinal);

        var timeoutMs = Math.Clamp(input.TimeoutMs ?? _options.DefaultTimeoutMs, 100, _options.MaximumTimeoutMs);
        var outputCap = Math.Clamp(input.MaxOutputBytes ?? _options.MaximumOutputBytes, 1024, _options.MaximumOutputBytes);

        List<Statement> program;
        try
        {
            program = ParseProgram(input.Script);
        }
        catch (ScriptException failure)
        {
            return CodemodeExecuteResult.Failed(failure.Failure, Elapsed(start));
        }

        var state = new ScriptState(session, granted, allowList, requestId);
        JsonNode? emitted;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        state.Token = deadline.Token;
        try
        {
            var body = ExecuteProgramAsync(program, state, deadline.Token);
            _ = body.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await body.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 250), CancellationToken.None);
            emitted = state.HasOutput ? state.Output : state.LastCall;
        }
        catch (TimeoutException)
        {
            return TimedOut(state, timeoutMs, start);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOut(state, timeoutMs, start);
        }
        catch (ScriptException failure)
        {
            return new CodemodeExecuteResult(false, string.Empty, failure.Failure, false,
                state.CapabilitiesInvoked, state.Executed, Elapsed(start));
        }

        var (output, truncated) = ClampUtf8(Render(emitted), outputCap);
        return new CodemodeExecuteResult(true, output, null, truncated,
            state.CapabilitiesInvoked, state.Executed, Elapsed(start));
    }

    private static CodemodeExecuteResult TimedOut(ScriptState state, int timeoutMs, long start) =>
        new(false, string.Empty,
            new CodemodeFailure(CodemodeFailureCodes.Timeout, $"The script exceeded its {timeoutMs} ms budget."),
            false, state.CapabilitiesInvoked, state.Executed, Elapsed(start));

    // ----- parsing -------------------------------------------------------------------------------

    private List<Statement> ParseProgram(string script)
    {
        var statements = new List<Statement>();
        var lines = script.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            // Tabs are normalised so argument splitting never has to consider two separators.
            var line = lines[lineIndex].Replace('\t', ' ').Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var keyword = ReadKeyword(line, out var rest);
            var index = statements.Count;
            switch (keyword)
            {
                case "call":
                {
                    rest = rest.Trim();
                    if (rest.Length == 0)
                        throw Invalid(index, lineIndex, "call needs a capability name.");
                    var split = rest.IndexOf(' ');
                    var capability = split < 0 ? rest : rest[..split];
                    var expression = split < 0 ? "{}" : rest[(split + 1)..].Trim();
                    if (!IsCapabilityName(capability))
                        throw Invalid(index, lineIndex, $"'{capability}' is not a capability name.");
                    statements.Add(new Statement(StatementKind.Call, index, lineIndex, capability,
                        expression.Length == 0 ? "{}" : expression));
                    break;
                }
                case "let":
                {
                    rest = rest.Trim();
                    var equals = rest.IndexOf('=');
                    if (equals <= 0)
                        throw Invalid(index, lineIndex, "let needs '<name> = <expression>'.");
                    var name = rest[..equals].Trim();
                    var expression = rest[(equals + 1)..].Trim();
                    if (!IsIdentifier(name))
                        throw Invalid(index, lineIndex, $"'{name}' is not a plain identifier.");
                    if (expression.Length == 0)
                        throw Invalid(index, lineIndex, "let needs an expression.");
                    statements.Add(new Statement(StatementKind.Let, index, lineIndex, name, expression));
                    break;
                }
                case "emit":
                {
                    var expression = rest.Trim();
                    if (expression.Length == 0)
                        throw Invalid(index, lineIndex, "emit needs an expression.");
                    statements.Add(new Statement(StatementKind.Emit, index, lineIndex, string.Empty, expression));
                    break;
                }
                default:
                    throw Invalid(index, lineIndex,
                        $"only 'call', 'let' and 'emit' are accepted (found '{keyword}').");
            }
        }

        if (statements.Count > _options.MaximumStatements)
            throw new ScriptException(new CodemodeFailure(CodemodeFailureCodes.BudgetExceeded,
                $"The script has {statements.Count} statements; the limit is {_options.MaximumStatements}."));

        return statements;
    }

    private static string ReadKeyword(string line, out string rest)
    {
        var index = 0;
        while (index < line.Length && char.IsAsciiLetter(line[index]))
            index++;
        var keyword = line[..index].ToLowerInvariant();
        rest = line[index..];
        return keyword;
    }

    private static bool IsCapabilityName(string value)
    {
        if (value.Length is 0 or > 128)
            return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
                return false;
        }
        return true;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length is 0 or > 64 || char.IsAsciiDigit(value[0]))
            return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
                return false;
        }
        return true;
    }

    private static ScriptException Invalid(int statement, int line, string message) =>
        new(new CodemodeFailure(CodemodeFailureCodes.ScriptInvalid,
            $"Statement {statement} (line {line + 1}): {message}", statement));

    // ----- execution -----------------------------------------------------------------------------

    private async Task ExecuteProgramAsync(List<Statement> program, ScriptState state, CancellationToken token)
    {
        foreach (var statement in program)
        {
            token.ThrowIfCancellationRequested();
            switch (statement.Kind)
            {
                case StatementKind.Call:
                {
                    if (state.Calls >= _options.MaximumCapabilityCalls)
                        throw new ScriptException(new CodemodeFailure(CodemodeFailureCodes.BudgetExceeded,
                            $"The script exceeded its {_options.MaximumCapabilityCalls}-call budget.",
                            statement.Index, statement.Name));
                    var value = await CallAsync(statement, state, token);
                    state.Bind("#" + state.Calls.ToString(CultureInfo.InvariantCulture), value);
                    state.Bind("_", value);
                    state.Calls++;
                    break;
                }
                case StatementKind.Let:
                    state.Bind(statement.Name, Evaluate(statement.Expression, state, statement));
                    break;
                default:
                    state.Output = Evaluate(statement.Expression, state, statement);
                    state.HasOutput = true;
                    break;
            }
            state.Executed++;
        }
    }

    /// <summary>
    /// One capability call. The name is re-checked here, immediately before use, so a script cannot
    /// reach anything the gate rejected even if a future statement form forgets to validate.
    /// </summary>
    private async Task<JsonNode?> CallAsync(Statement statement, ScriptState state, CancellationToken token)
    {
        var capability = statement.Name;
        if (ValidateCall(statement, state) is { } refusal)
            throw new ScriptException(refusal);

        var argument = Evaluate(statement.Expression, state, statement);
        var json = argument switch
        {
            null => "{}",
            JsonObject payload => payload.ToJsonString(Json),
            _ => throw new ScriptException(new CodemodeFailure(CodemodeFailureCodes.ScriptInvalid,
                $"Statement {statement.Index}: a capability call needs a JSON object payload.",
                statement.Index, capability))
        };

        var request = new RequestCapability
        {
            RequestId = $"{state.RequestId}-{state.Calls.ToString(CultureInfo.InvariantCulture)}",
            RequestingAgentId = state.Session.AgentId,
            Capability = capability,
            ContentType = "application/json",
            Payload = JsonPayload.FromUtf8(json)
        };
        state.CapabilitiesInvoked.Add(capability);

        CapabilityResult? result = null;
        await foreach (var item in _gateway.InvokeAsync(state.Session, request, token))
        {
            result = item;
            break;
        }

        if (result is null)
            throw new ScriptException(new CodemodeFailure(CodemodeFailureCodes.CapabilityFailed,
                $"'{capability}' returned no result.", statement.Index, capability));
        if (!result.Succeeded)
            throw new ScriptException(new CodemodeFailure(CodemodeFailureCodes.CapabilityFailed,
                $"'{capability}' failed: {Sanitize(result.Error)}", statement.Index, capability));

        if (result.Payload.Length > 262144) throw Unresolved(statement, "the capability response exceeds the intermediate value budget.");
        return CopyValue(ReadPayload(result.Payload), state, statement, false, 0);
    }

    private static CodemodeFailure? ValidateCall(Statement statement, ScriptState state)
    {
        var capability = statement.Name;
        if (CodemodeCapabilities.All.Contains(capability))
            return new(CodemodeFailureCodes.CapabilityNotAllowed, "A script cannot invoke codemode.",
                statement.Index, capability);
        if (IsDenied(capability))
            return new(CodemodeFailureCodes.CapabilityNotAllowed,
                $"'{capability}' is not reachable from a script.", statement.Index, capability);
        if (state.AllowList is { } allowList && !allowList.Contains(capability))
            return new(CodemodeFailureCodes.CapabilityNotAllowed,
                $"'{capability}' is outside the allow-list supplied with this script.", statement.Index, capability);
        if (!state.Granted.Contains(capability))
            return new(CodemodeFailureCodes.CapabilityNotGranted,
                $"'{capability}' is not granted to this installation.", statement.Index, capability);
        if (CapabilityRiskCatalog.Classify(capability) != CapabilityRiskClass.Read)
            return new(CodemodeFailureCodes.CapabilityNotRead,
                $"'{capability}' is not read-only, so a script may not call it.", statement.Index, capability);
        return null;
    }

    // ----- expressions ---------------------------------------------------------------------------

    private static JsonNode? Evaluate(string expression, ScriptState state, Statement statement)
    {
        var text = expression.Trim();
        if (text.StartsWith('$'))
            return Resolve(text, state, statement);

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text, null, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new ScriptException(new CodemodeFailure(CodemodeFailureCodes.ScriptInvalid,
                $"Statement {statement.Index}: the expression is not valid JSON.", statement.Index,
                statement.Name.Length == 0 ? null : statement.Name));
        }
        return Substitute(parsed, state, statement);
    }

    /// <summary>
    /// Rewrites a JSON literal, replacing string leaves that start with '$' with the value they refer to.
    /// A literal dollar sign is written '$$'. Nothing is ever evaluated: a substituted value is data
    /// copied into a cloned literal, so a script cannot hand a capability a node it does not own.
    /// </summary>
    private static JsonNode? Substitute(JsonNode? node, ScriptState state, Statement statement) =>
        CopyValue(node, state, statement, true, 0);

    private static JsonNode? CopyValue(JsonNode? node, ScriptState state, Statement statement, bool substitute, int depth)
    {
        state.Token.ThrowIfCancellationRequested();
        if (++state.ExpandedNodes > 32768 || depth > 32)
            throw Unresolved(statement, "the intermediate value budget was exceeded.");
        switch (node)
        {
            case JsonObject source:
                var obj = new JsonObject();
                foreach (var pair in source)
                {
                    state.ExpandedBytes += Encoding.UTF8.GetByteCount(pair.Key) + 4;
                    CheckBytes();
                    obj[pair.Key] = CopyValue(pair.Value, state, statement, substitute, depth + 1);
                }
                return obj;
            case JsonArray source:
                var array = new JsonArray();
                foreach (var value in source) array.Add(CopyValue(value, state, statement, substitute, depth + 1));
                return array;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                state.ExpandedBytes += Encoding.UTF8.GetByteCount(text) + 2;
                CheckBytes();
                if (substitute && text.StartsWith('$')) return Resolve(text, state, statement);
                return JsonValue.Create(text);
            default:
                state.ExpandedBytes += 32;
                CheckBytes();
                return node?.DeepClone();
        }
        void CheckBytes()
        {
            if (state.ExpandedBytes > 524288) throw Unresolved(statement, "the intermediate value budget was exceeded.");
        }
    }

    /// <summary>
    /// Resolves <c>$name</c>, <c>$&lt;n&gt;</c> (call index), <c>$_</c> (last call), each optionally
    /// followed by <c>.property</c> and <c>[index]</c> segments. There is no function call, no operator
    /// and no way to name anything outside the binding table.
    /// </summary>
    private static JsonNode? Resolve(string reference, ScriptState state, Statement statement)
    {
        if (reference.Length >= 2 && reference[1] == '$')
            return JsonValue.Create(reference[1..]);

        var index = 1;
        var nameStart = index;
        while (index < reference.Length &&
               (char.IsAsciiLetterOrDigit(reference[index]) || reference[index] == '_'))
            index++;
        if (index == nameStart)
            throw Unresolved(statement, "a '$' must be followed by a name or a call index.");

        var name = reference[nameStart..index];
        var key = name.All(char.IsAsciiDigit) ? "#" + name : name;
        if (!state.TryGet(key, out var current))
            throw Unresolved(statement, $"'{name}' has not been bound before this statement.");

        while (index < reference.Length)
        {
            if (reference[index] == '.')
            {
                index++;
                var start = index;
                while (index < reference.Length &&
                       (char.IsAsciiLetterOrDigit(reference[index]) || reference[index] == '_'))
                    index++;
                if (index == start)
                    throw Unresolved(statement, "a '.' must be followed by a property name.");
                var property = reference[start..index];
                if (current is JsonObject obj && obj.TryGetPropertyValue(property, out var child))
                    current = child;
                else
                    throw Unresolved(statement, $"'{name}' has no property '{property}'.");
            }
            else if (reference[index] == '[')
            {
                index++;
                var start = index;
                while (index < reference.Length && char.IsAsciiDigit(reference[index]))
                    index++;
                if (index == start || index >= reference.Length || reference[index] != ']')
                    throw Unresolved(statement, "a '[' must contain an index and close with ']'.");
                if (!int.TryParse(reference[start..index], NumberStyles.None, CultureInfo.InvariantCulture,
                        out var position))
                    throw Unresolved(statement, "the index is out of range.");
                index++;
                if (current is JsonArray array && position < array.Count)
                    current = array[position];
                else
                    throw Unresolved(statement, $"'{name}' has no element {position}.");
            }
            else
            {
                throw Unresolved(statement, "only '.property' and '[index]' may follow a reference.");
            }
        }

        return CopyValue(current, state, statement, false, 0);
    }

    private static ScriptException Unresolved(Statement statement, string message) =>
        new(new CodemodeFailure(CodemodeFailureCodes.ScriptInvalid,
            $"Statement {statement.Index}: {message}", statement.Index,
            statement.Name.Length == 0 ? null : statement.Name));

    // ----- values --------------------------------------------------------------------------------

    /// <summary>Wraps a nested payload as data, whether the capability returned JSON or plain text.</summary>
    private static JsonNode? ReadPayload(JsonPayload payload)
    {
        if (payload.IsEmpty)
            return null;
        try
        {
            return JsonNode.Parse(payload.Span, null, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return JsonValue.Create(payload.ToStringUtf8());
        }
    }

    /// <summary>A JSON string emits as its raw text so an agent does not pay for escaping.</summary>
    private static string Render(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        var node => node.ToJsonString(Json)
    };

    /// <summary>Cuts on a UTF-8 boundary and reports the cut instead of failing the call.</summary>
    private static (string Text, bool Truncated) ClampUtf8(string text, int maximumBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maximumBytes)
            return (text, false);
        var cut = maximumBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;
        return (Encoding.UTF8.GetString(bytes, 0, cut), true);
    }

    /// <summary>Flattens a nested handler's own (already curated) message to one short line.</summary>
    private static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "the capability reported no reason";
        var flattened = message.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= MaximumNestedErrorCharacters
            ? flattened
            : flattened[..MaximumNestedErrorCharacters] + "...";
    }

    // ----- script state --------------------------------------------------------------------------

    private enum StatementKind
    {
        Call,
        Let,
        Emit
    }

    /// <summary>A parsed statement. <see cref="Name"/> is the capability name for a call and the bound name for a let.</summary>
    private sealed record Statement(StatementKind Kind, int Index, int Line, string Name, string Expression);

    /// <summary>Everything one script run may touch: its grant, its bindings and its counters.</summary>
    private sealed class ScriptState(
        AgentSession session,
        IReadOnlySet<string> granted,
        HashSet<string>? allowList,
        string requestId)
    {
        private readonly Dictionary<string, JsonNode?> _bindings = new(StringComparer.Ordinal);

        public AgentSession Session { get; } = session;

        public IReadOnlySet<string> Granted { get; } = granted;

        public HashSet<string>? AllowList { get; } = allowList;

        public string RequestId { get; } = requestId;

        public List<string> CapabilitiesInvoked { get; } = [];

        public CancellationToken Token { get; set; }
        public int ExpandedNodes { get; set; }
        public int ExpandedBytes { get; set; }
        public int Calls { get; set; }

        public int Executed { get; set; }

        public JsonNode? Output { get; set; }

        public bool HasOutput { get; set; }

        public JsonNode? LastCall => _bindings.TryGetValue("_", out var value) ? value : null;

        public void Bind(string key, JsonNode? value) => _bindings[key] = value;

        public bool TryGet(string key, out JsonNode? value) => _bindings.TryGetValue(key, out value);
    }

    /// <summary>
    /// Carries a caller-safe failure out of the interpreter. The inner exception message is the same
    /// text the caller sees, so nothing internal can leak by accident through a wrapping type.
    /// </summary>
    private sealed class ScriptException(CodemodeFailure failure) : Exception(failure.Message)
    {
        public CodemodeFailure Failure { get; } = failure;
    }
}
