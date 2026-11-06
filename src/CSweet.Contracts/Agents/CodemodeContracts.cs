namespace CSweet.Contracts.Agents;

/// <summary>
/// The capability names the broker's codemode handler answers to. Codemode replaces a dozen
/// sequential read-only round trips with one script: the agent submits a short program that calls
/// capabilities it has already been granted and only the final value comes back as the tool result.
/// </summary>
public static class CodemodeCapabilities
{
    /// <summary>Run one read-only script over already-granted <see cref="CapabilityRiskClass.Read"/> capabilities.</summary>
    public const string Execute = "platform.codemode.execute.v1";

    /// <summary>
    /// Describe what a script may call and how large it may be, for this installation, without
    /// executing anything. Inert: it reads the grant, never the workspace.
    /// </summary>
    public const string Manifest = "platform.codemode.manifest.v1";

    /// <summary>Every capability name in this family.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Execute, Manifest };
}

/// <summary>
/// Stable, caller-safe failure codes. These are the only failure vocabulary a script author sees;
/// handler exception text never reaches them.
/// </summary>
public static class CodemodeFailureCodes
{
    /// <summary>Codemode is switched off for this deployment (the default).</summary>
    public const string Disabled = "codemode_disabled";

    /// <summary>The caller may not use codemode at all, or the request could not be authorised.</summary>
    public const string Denied = "codemode_denied";

    /// <summary>The script text could not be parsed, or referenced nothing that exists.</summary>
    public const string ScriptInvalid = "codemode_script_invalid";

    /// <summary>The script named a capability outside its own allow-list, or a denied one.</summary>
    public const string CapabilityNotAllowed = "codemode_capability_not_allowed";

    /// <summary>The script named a capability this installation was never granted.</summary>
    public const string CapabilityNotGranted = "codemode_capability_not_granted";

    /// <summary>The script named a capability that is not read-only, so a script may not reach it.</summary>
    public const string CapabilityNotRead = "codemode_capability_not_read";

    /// <summary>The invoked capability itself failed; its own curated error is quoted back.</summary>
    public const string CapabilityFailed = "codemode_capability_failed";

    /// <summary>The script exceeded its statement or capability-call budget.</summary>
    public const string BudgetExceeded = "codemode_budget_exceeded";

    /// <summary>The script exceeded its wall-clock budget.</summary>
    public const string Timeout = "codemode_timeout";

    /// <summary>Something behind codemode failed unexpectedly. Deliberately opaque: no exception text.</summary>
    public const string Internal = "codemode_internal";
}

/// <summary>
/// One script submission. Everything here is a request: the handler clamps the timeout and output
/// cap against its own configured maximums, so a large number in this payload cannot widen a limit.
/// </summary>
/// <param name="Script">The program text. Line oriented, one statement per line, <c>#</c> comments.</param>
/// <param name="AllowedCapabilities">
/// Optional allow-list of capability names this script may call, on top of what the installation has
/// been granted. When absent, every granted read-only capability is callable (still subject to the
/// handler's own deny list).
/// </param>
/// <param name="TimeoutMs">Requested wall-clock budget. Clamped to the deployment maximum.</param>
/// <param name="MaxOutputBytes">Requested output cap. Clamped to the deployment maximum.</param>
public sealed record CodemodeExecuteRequest(
    string Script,
    IReadOnlyList<string>? AllowedCapabilities = null,
    int? TimeoutMs = null,
    int? MaxOutputBytes = null);

/// <summary>A caller-safe failure. Deliberately free text only: no exception types, no stack, no paths.</summary>
/// <param name="Code">One of <see cref="CodemodeFailureCodes"/>.</param>
/// <param name="Message">A short explanation that is safe to hand back to the agent.</param>
/// <param name="Statement">Zero-based statement index the failure was raised at, when known.</param>
/// <param name="Capability">The capability named by the failing statement, when known.</param>
public sealed record CodemodeFailure(
    string Code,
    string Message,
    int? Statement = null,
    string? Capability = null);

/// <summary>
/// The single tool result for a whole script. <see cref="Stdout"/> is the only content the agent
/// receives; <see cref="Truncated"/> says whether the output cap was hit (the text is still valid --
/// it is cut on a UTF-8 boundary rather than replaced with an error).
/// </summary>
/// <param name="Succeeded">False when the script failed; <see cref="Error"/> explains why.</param>
/// <param name="Stdout">The emitted value as text (a JSON string emits as its raw text).</param>
/// <param name="Error">Structured failure, or null on success.</param>
/// <param name="Truncated">True when <see cref="Stdout"/> was cut at the output byte cap.</param>
/// <param name="CapabilitiesInvoked">Capability names the script actually called, in order.</param>
/// <param name="StatementsExecuted">How many statements ran before the script ended.</param>
/// <param name="ElapsedMs">Wall-clock milliseconds spent inside the script.</param>
public sealed record CodemodeExecuteResult(
    bool Succeeded,
    string Stdout,
    CodemodeFailure? Error,
    bool Truncated,
    IReadOnlyList<string> CapabilitiesInvoked,
    int StatementsExecuted,
    long ElapsedMs)
{
    /// <summary>A failure with an empty output, so callers never parse a half-written value.</summary>
    public static CodemodeExecuteResult Failed(CodemodeFailure failure, long elapsedMs) =>
        new(false, string.Empty, failure, false, Array.Empty<string>(), 0, elapsedMs);
}

/// <summary>One statement form the interpreter accepts, described for the agent that has to write it.</summary>
/// <param name="Keyword">The leading keyword, e.g. <c>call</c>.</param>
/// <param name="Form">The syntax, e.g. <c>call &lt;capability&gt; &lt;json-expression&gt;</c>.</param>
/// <param name="Meaning">What executing it does.</param>
public sealed record CodemodeStatementSpec(string Keyword, string Form, string Meaning);

/// <summary>
/// What a script may do in this deployment, computed for the calling installation. This is the
/// discoverable half of codemode: an agent can read it instead of guessing at names, and an operator
/// can see exactly what was enabled.
/// </summary>
public sealed record CodemodeManifest
{
    /// <summary>The capability name that returns this manifest.</summary>
    public required string Capability { get; init; }

    /// <summary>Whether <see cref="CodemodeCapabilities.Execute"/> will run at all in this deployment.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Language tag for the statement grammar below.</summary>
    public required string Language { get; init; }

    /// <summary>The statement forms accepted by the interpreter.</summary>
    public required IReadOnlyList<CodemodeStatementSpec> Statements { get; init; }

    /// <summary>Capability names this installation may call from a script (granted and read-only).</summary>
    public required IReadOnlyList<string> AllowedCapabilities { get; init; }

    /// <summary>Read-only capabilities that are still refused by name, e.g. because they leave the instance.</summary>
    public required IReadOnlyList<string> DeniedCapabilities { get; init; }

    /// <summary>Name prefixes that are refused regardless of how they are classified.</summary>
    public required IReadOnlyList<string> DeniedCapabilityPrefixes { get; init; }

    /// <summary>The only risk classes a script may reach. Always exactly <c>Read</c>.</summary>
    public required IReadOnlyList<string> AllowedRiskClasses { get; init; }

    /// <summary>Script size cap in bytes.</summary>
    public required int MaximumScriptBytes { get; init; }

    /// <summary>Statement cap.</summary>
    public required int MaximumStatements { get; init; }

    /// <summary>Capability-call cap within one script.</summary>
    public required int MaximumCapabilityCalls { get; init; }

    /// <summary>Wall-clock budget used when the request asks for none.</summary>
    public required int DefaultTimeoutMs { get; init; }

    /// <summary>Largest wall-clock budget a request may ask for.</summary>
    public required int MaximumTimeoutMs { get; init; }

    /// <summary>Output byte cap applied to <see cref="CodemodeExecuteResult.Stdout"/>.</summary>
    public required int MaximumOutputBytes { get; init; }

    /// <summary>Always false: the interpreter has no file primitives and no capability it can reach touches the filesystem.</summary>
    public required bool FilesystemAccess { get; init; }

    /// <summary>Always false: the interpreter has no socket primitives and egress capabilities are refused by name.</summary>
    public required bool NetworkAccess { get; init; }

    /// <summary>Always false: a script cannot start another script.</summary>
    public required bool NestedScripts { get; init; }
}
