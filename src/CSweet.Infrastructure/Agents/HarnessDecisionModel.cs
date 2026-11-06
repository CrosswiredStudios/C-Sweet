using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Agents;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.Agents;

/// <summary>
/// Optional decision-model (TypeSafe / Jev "SystemOne") engine used by decision compaction. Bound from
/// <c>CSweet:Harness:DecisionModel</c>; environment variables use <c>CSweet__Harness__DecisionModel__Key</c>.
/// When no engine resolves, every request behaves exactly as without this feature.
/// </summary>
public sealed class HarnessDecisionModelOptions
{
    public const string SectionName = "CSweet:Harness:DecisionModel";

    public bool Enabled { get; set; } = true;
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Names the environment variable holding the key. When set, it is the only key source.</summary>
    public string? KeyEnv { get; set; }
    public int TimeoutMs { get; set; } = 20000;
    public int Retries { get; set; } = 2;
    public int MaxQuestionsPerRequest { get; set; } = 24;
    public int MaxRequestTokens { get; set; } = 30000;
    public int MaxParallel { get; set; } = 4;
    public int MaxStateTokens { get; set; } = 25000;
    public int PreserveRecent { get; set; } = 6;
    public double KeepThreshold { get; set; } = 0.5;
    public int TruncateHeadChars { get; set; } = 300;

    public static HarnessDecisionModelOptions From(IConfiguration? configuration)
    {
        var options = configuration?.GetSection(SectionName).Get<HarnessDecisionModelOptions>() ?? new HarnessDecisionModelOptions();
        return options.Normalize();
    }

    /// <summary>Forces every limit into the range the client and compactor defend.</summary>
    public HarnessDecisionModelOptions Normalize()
    {
        TimeoutMs = Math.Clamp(TimeoutMs, 1000, 180000);
        Retries = Math.Clamp(Retries, 0, 5);
        MaxQuestionsPerRequest = Math.Clamp(MaxQuestionsPerRequest, 1, 24);
        MaxRequestTokens = Math.Clamp(MaxRequestTokens, 1000, 1_000_000);
        MaxParallel = Math.Clamp(MaxParallel, 1, 16);
        MaxStateTokens = Math.Max(MaxStateTokens, 1000);
        PreserveRecent = Math.Max(PreserveRecent, 1);
        KeepThreshold = double.IsFinite(KeepThreshold) ? Math.Clamp(KeepThreshold, 0, 1) : 0.5;
        TruncateHeadChars = Math.Clamp(TruncateHeadChars, 0, 100_000);
        return this;
    }
}

/// <summary>A resolved decision engine. The key is internal and never serialized, logged or audited.</summary>
public sealed class HarnessDecisionEngine
{
    private HarnessDecisionEngine(bool available, Uri? endpoint, string? model, string? apiKey, string? reason, string source)
    {
        Available = available; Endpoint = endpoint; Model = model; ApiKey = apiKey; Reason = reason; Source = source;
    }

    public bool Available { get; }
    public Uri? Endpoint { get; }
    public string? Model { get; }
    public string? Reason { get; }
    /// <summary>local, configured, fallback or default.</summary>
    public string Source { get; }
    internal string? ApiKey { get; }
    public string? Host => Endpoint?.IsDefaultPort == false ? Endpoint.Authority : Endpoint?.Host;

    public HarnessDecisionEngineInfo Describe() => new(Available, Available ? Host : null, Available ? Model : null, Available ? null : Reason);
    public override string ToString() => Available ? $"{Host} ({Model})" : $"unavailable: {Reason}";

    public static HarnessDecisionEngine Unavailable(string reason) => new(false, null, null, null, reason, "none");
    internal static HarnessDecisionEngine Create(Uri endpoint, string model, string? key, string source) => new(true, endpoint, model, key, null, source);
}

/// <summary>Engine discovery, mirroring nur-cli's <c>client::api_key</c> and <c>fallback_engine</c> order.</summary>
public static class HarnessDecisionEngineResolver
{
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-latest";
    public const string TypeSafeHost = "api.typesafe.ai";
    public const string LocalUrlVariable = "CSWEET_JEV_LOCAL_URL";
    public static readonly string[] TypeSafeKeyVariables = ["TYPESAFE_API_KEY", "TYPESAFE_KEY", "JEV_API_KEY"];

    private static readonly (string Variable, string Endpoint, string Model)[] FallbackEngines =
    [
        ("SUBQ_API_KEY", "https://api.subq.ai/v1/systemone", "subq-snap-preview"),
        ("OPENROUTER_API_KEY", "https://openrouter.ai/api/alpha/decisions", "inception/mercury-decide"),
        ("LIQUID_AI_API_KEY", "https://api.liquid.ai/decisions/v1/systemone", "d1-ultra"),
    ];

    /// <summary>Resolves the process engine from configuration and the real environment.</summary>
    public static HarnessDecisionEngine Resolve(HarnessDecisionModelOptions options) =>
        Resolve(options, Environment.GetEnvironmentVariable,
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase));

    public static HarnessDecisionEngine Resolve(HarnessDecisionModelOptions options, Func<string, string?> environment, bool inContainer = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return HarnessDecisionEngine.Unavailable("Decision-model compaction is disabled by configuration.");
        string? Env(string name) => Blank(environment(name));
        var local = Env(LocalUrlVariable);
        var configuredUrl = Blank(options.BaseUrl);
        var configuredKey = Blank(options.ApiKey);
        var keyEnv = Blank(options.KeyEnv);
        var typeSafeKey = TypeSafeKeyVariables.Select(Env).FirstOrDefault(x => x is not null);
        (string Endpoint, string Model, string Key)? fallback = null;
        if (local is null && configuredUrl is null && configuredKey is null && keyEnv is null && typeSafeKey is null)
            foreach (var engine in FallbackEngines)
                if (Env(engine.Variable) is { } key) { fallback = (engine.Endpoint, engine.Model, key); break; }

        string raw, source;
        if (local is not null) { raw = NormalizeLocal(local); source = "local"; }
        else if (configuredUrl is not null) { raw = configuredUrl; source = "configured"; }
        else if (fallback is { } chosen) { raw = chosen.Endpoint; source = "fallback"; }
        else { raw = DefaultEndpoint; source = "default"; }
        if (!TryValidate(raw, out var endpoint, out var error)) return HarnessDecisionEngine.Unavailable(error);

        string? apiKey;
        if (keyEnv is not null)
        {
            apiKey = Env(keyEnv);
            if (apiKey is null && !endpoint.IsLoopback)
                return HarnessDecisionEngine.Unavailable($"The configured key variable {keyEnv} is not set.");
        }
        else if (endpoint.IsLoopback) apiKey = null;
        else if (configuredKey is not null) apiKey = configuredKey;
        else if (source == "fallback") apiKey = fallback!.Value.Key;
        else if (string.Equals(endpoint.Host, TypeSafeHost, StringComparison.OrdinalIgnoreCase)) apiKey = typeSafeKey;
        else apiKey = null;
        if (apiKey is null && !endpoint.IsLoopback)
            return HarnessDecisionEngine.Unavailable("No decision-model API key is configured.");

        var model = Blank(options.Model) ?? (source == "fallback" ? fallback!.Value.Model : null) ?? DefaultModel;
        // Same rule as OpenAiCompatibleLlmProviderFactory: a loopback engine is the container host.
        if (inContainer && endpoint.IsLoopback) endpoint = new UriBuilder(endpoint) { Host = "host.docker.internal" }.Uri;
        return HarnessDecisionEngine.Create(endpoint, model, apiKey, source);
    }

    /// <summary>A bare origin or a <c>/v1</c> base gets the SystemOne path.</summary>
    public static string NormalizeLocal(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return value;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 || path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(uri) { Path = "/v1/systemone" }.Uri.ToString();
        return uri.ToString();
    }

    /// <summary>HTTPS, or HTTP only on loopback. No userinfo, query or fragment.</summary>
    public static bool TryValidate(string value, out Uri endpoint, out string error)
    {
        endpoint = null!; error = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        { error = "The decision-model endpoint must be an absolute HTTPS URL."; return false; }
        if (uri.Scheme == "http" && !uri.IsLoopback)
        { error = "The decision-model endpoint must use HTTPS unless it is on loopback."; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { error = "The decision-model endpoint must not contain credentials, a query or a fragment."; return false; }
        endpoint = uri; return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Tokenizer-free estimate from nur-cli <c>compact::estimate_tokens</c>; deliberately a little high.</summary>
public static class HarnessDecisionTokens
{
    public static long Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsAsciiLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                tokens += 1 + (i - start - 1) / 6;
            }
            else if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                tokens += (i - start) / 2.0;
            }
            else
            {
                // A surrogate pair is one character, as in Rust's chars().
                if (!char.IsWhiteSpace(c)) tokens += 0.9;
                i += char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            }
        }
        return (long)Math.Ceiling(tokens);
    }
}

public sealed record HarnessDecisionQuestion(string Id, string Instructions, string WhenTrue, string WhenFalse);

/// <summary>Answered probabilities by question id. A malformed or missing answer is simply absent.</summary>
public sealed record HarnessDecisionAnswers(IReadOnlyDictionary<string, double> Answers, int Requests,
    long InputTokens, long OutputTokens, long StateTokens);

/// <summary>The decision engine could not answer. Callers keep their content unchanged.</summary>
public sealed class HarnessDecisionException(string message) : Exception(message);

/// <summary>SystemOne <c>noul</c> client. Redirects are disabled; the overall deadline includes retry backoff.</summary>
public sealed class HarnessDecisionClient(HttpClient http, HarnessDecisionModelOptions options)
{
    public const int RequestOverheadTokens = 20;
    private static readonly string UserAgent = "C-Sweet/" + ((typeof(HarnessDecisionClient).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HarnessDecisionClient).Assembly.GetName().Version?.ToString() ?? "0").Split('+')[0]);

    /// <summary>Splits questions into requests that respect the count and token budgets.</summary>
    public static IReadOnlyList<IReadOnlyList<HarnessDecisionQuestion>> Plan(long stateTokens,
        IReadOnlyList<HarnessDecisionQuestion> questions, int maxQuestions, int maxTokens)
    {
        var room = maxTokens - stateTokens - RequestOverheadTokens;
        if (room <= 0) throw new HarnessDecisionException("The decision state leaves no room for questions within the request budget.");
        var chunks = new List<IReadOnlyList<HarnessDecisionQuestion>>();
        var current = new List<HarnessDecisionQuestion>();
        long used = 0;
        foreach (var question in questions)
        {
            var cost = HarnessDecisionTokens.Estimate(question.Id + QuestionNode(question).ToJsonString());
            if (cost > room) throw new HarnessDecisionException("A decision question does not fit within the request budget.");
            if (current.Count > 0 && (current.Count >= maxQuestions || used + cost > room)) { chunks.Add(current); current = []; used = 0; }
            current.Add(question); used += cost;
        }
        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    public async Task<HarnessDecisionAnswers> AskAsync(HarnessDecisionEngine engine, JsonNode state,
        IReadOnlyList<HarnessDecisionQuestion> questions, CancellationToken token)
    {
        if (!engine.Available || engine.Endpoint is null) throw new HarnessDecisionException(engine.Reason ?? "No decision engine is configured.");
        var stateJson = state.ToJsonString();
        var stateTokens = HarnessDecisionTokens.Estimate(stateJson);
        var chunks = Plan(stateTokens, questions, options.MaxQuestionsPerRequest, options.MaxRequestTokens);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.TimeoutMs);
        using var gate = new SemaphoreSlim(options.MaxParallel);
        var tasks = chunks.Select(async chunk =>
        {
            await gate.WaitAsync(deadline.Token);
            try { return await SendAsync(engine, Body(engine, stateJson, chunk), deadline.Token); }
            catch { deadline.Cancel(); throw; } // One failed chunk fails the whole judgment.
            finally { gate.Release(); }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            var failure = tasks.Where(x => x.IsFaulted).SelectMany(x => x.Exception!.InnerExceptions).OfType<HarnessDecisionException>().FirstOrDefault();
            throw failure ?? new HarnessDecisionException("The decision engine did not answer before the deadline.");
        }
        var answers = new Dictionary<string, double>(StringComparer.Ordinal);
        long input = 0, output = 0;
        foreach (var result in tasks.Select(x => x.Result))
        {
            foreach (var answer in result.Answers) answers[answer.Key] = answer.Value;
            input += result.Input; output += result.Output;
        }
        return new(answers, chunks.Count, input, output, stateTokens);
    }

    private static JsonObject QuestionNode(HarnessDecisionQuestion question) => new()
    {
        ["type"] = "noul",
        ["instructions"] = question.Instructions,
        ["criteria"] = new JsonObject { ["true"] = question.WhenTrue, ["false"] = question.WhenFalse },
    };

    private static byte[] Body(HarnessDecisionEngine engine, string stateJson, IReadOnlyList<HarnessDecisionQuestion> chunk)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("state"); writer.WriteRawValue(stateJson, skipInputValidation: true);
            writer.WriteString("model", engine.Model);
            writer.WritePropertyName("questions"); writer.WriteStartObject();
            foreach (var question in chunk) { writer.WritePropertyName(question.Id); QuestionNode(question).WriteTo(writer); }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private async Task<(Dictionary<string, double> Answers, long Input, long Output)> SendAsync(HarnessDecisionEngine engine, byte[] body, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, engine.Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (engine.ApiKey is { } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            string reason;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return Parse(await response.Content.ReadAsByteArrayAsync(token));
                if (status is not (429 or 529) && status < 500)
                    throw new HarnessDecisionException($"The decision engine rejected the request (HTTP {status}).");
                reason = $"HTTP {status}";
            }
            catch (HttpRequestException) { reason = "transport failure"; }
            catch (IOException) { reason = "transport failure"; }
            if (attempt >= options.Retries)
                throw new HarnessDecisionException($"The decision engine was unavailable after {attempt + 1} attempts ({reason}).");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 200 * Math.Pow(3, attempt))), token);
        }
    }

    private static (Dictionary<string, double>, long, long) Parse(byte[] payload)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw new HarnessDecisionException("The decision engine returned invalid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new HarnessDecisionException("The decision engine returned an unexpected response.");
            var answers = new Dictionary<string, double>(StringComparer.Ordinal);
            if (root.TryGetProperty("answers", out var items) && items.ValueKind == JsonValueKind.Object)
                foreach (var item in items.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.Object &&
                        item.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "noul" &&
                        item.Value.TryGetProperty("noul", out var p) && p.ValueKind == JsonValueKind.Number &&
                        p.TryGetDouble(out var value) && double.IsFinite(value) && value is >= 0 and <= 1)
                        answers[item.Name] = value;
            long Usage(string name) => root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var n) && n >= 0 ? n : 0;
            return (answers, Usage("input_tokens"), Usage("output_tokens"));
        }
    }
}
