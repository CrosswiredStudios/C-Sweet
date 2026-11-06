using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Infrastructure.Agents;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class HarnessDecisionCompactionTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(x => x.Name == name).Value;

    [Fact]
    public void NoKeyAnywhereLeavesTheEngineUnavailable()
    {
        var engine = HarnessDecisionEngineResolver.Resolve(new(), Env());
        Assert.False(engine.Available);
        Assert.Null(engine.Describe().Host);
        Assert.False(HarnessDecisionEngineResolver.Resolve(new() { ApiKey = "k", Enabled = false }, Env()).Available);
    }

    [Fact]
    public void TypeSafeEnvironmentKeysApplyOnlyToTheTypeSafeHost()
    {
        var engine = HarnessDecisionEngineResolver.Resolve(new(), Env(("JEV_API_KEY", "jev"), ("TYPESAFE_KEY", "second")));
        Assert.True(engine.Available);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", engine.Endpoint!.ToString());
        Assert.Equal("jev-latest", engine.Model);
        Assert.Equal("second", engine.ApiKey);
        Assert.DoesNotContain("second", engine.ToString());
        Assert.DoesNotContain("second", JsonSerializer.Serialize(engine.Describe()));
        Assert.False(HarnessDecisionEngineResolver.Resolve(new() { BaseUrl = "https://decide.example/v1/systemone" },
            Env(("TYPESAFE_API_KEY", "ts"))).Available);
    }

    [Fact]
    public void KeyEnvIsTheOnlyKeySourceWhenConfigured()
    {
        var options = new HarnessDecisionModelOptions { KeyEnv = "DECIDE_KEY", ApiKey = "configured" };
        Assert.False(HarnessDecisionEngineResolver.Resolve(options, Env(("TYPESAFE_API_KEY", "ts"))).Available);
        Assert.Equal("from-env", HarnessDecisionEngineResolver.Resolve(options, Env(("DECIDE_KEY", "from-env"))).ApiKey);
        Assert.Equal("configured", HarnessDecisionEngineResolver.Resolve(new() { ApiKey = "configured" }, Env(("TYPESAFE_API_KEY", "ts"))).ApiKey);
    }

    [Fact]
    public void LoopbackEngineNeedsNoKeyAndMapsToTheContainerHost()
    {
        var local = HarnessDecisionEngineResolver.Resolve(new() { ApiKey = "unused" }, Env(("CSWEET_JEV_LOCAL_URL", "http://localhost:8080")));
        Assert.True(local.Available);
        Assert.Equal("http://localhost:8080/v1/systemone", local.Endpoint!.ToString());
        Assert.Null(local.ApiKey);
        Assert.Equal("http://127.0.0.1:9000/v1/systemone",
            HarnessDecisionEngineResolver.Resolve(new(), Env(("CSWEET_JEV_LOCAL_URL", "http://127.0.0.1:9000/v1"))).Endpoint!.ToString());
        var container = HarnessDecisionEngineResolver.Resolve(new(), Env(("CSWEET_JEV_LOCAL_URL", "http://localhost:8080")), inContainer: true);
        Assert.Equal("host.docker.internal", container.Endpoint!.Host);
    }

    [Theory]
    [InlineData("http://decide.example/v1/systemone")]
    [InlineData("https://user:pass@decide.example/v1/systemone")]
    [InlineData("https://decide.example/v1/systemone?key=1")]
    [InlineData("https://decide.example/v1/systemone#x")]
    [InlineData("ftp://decide.example/")]
    public void UnsafeEndpointsAreRefused(string url) =>
        Assert.False(HarnessDecisionEngineResolver.Resolve(new() { BaseUrl = url, ApiKey = "k" }, Env()).Available);

    [Fact]
    public void FallbackEnginesApplyInOrderOnlyWithoutExplicitConfiguration()
    {
        var subq = HarnessDecisionEngineResolver.Resolve(new(), Env(("OPENROUTER_API_KEY", "or"), ("SUBQ_API_KEY", "sq")));
        Assert.Equal(("api.subq.ai", "subq-snap-preview", "sq"), (subq.Host!, subq.Model!, subq.ApiKey!));
        var router = HarnessDecisionEngineResolver.Resolve(new(), Env(("OPENROUTER_API_KEY", "or"), ("LIQUID_AI_API_KEY", "lq")));
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", router.Endpoint!.ToString());
        Assert.Equal("inception/mercury-decide", router.Model);
        var liquid = HarnessDecisionEngineResolver.Resolve(new() { Model = "custom" }, Env(("LIQUID_AI_API_KEY", "lq")));
        Assert.Equal(("api.liquid.ai", "custom"), (liquid.Host!, liquid.Model!));
        Assert.Equal("api.typesafe.ai", HarnessDecisionEngineResolver.Resolve(new(), Env(("SUBQ_API_KEY", "sq"), ("TYPESAFE_API_KEY", "ts"))).Host);
        var configured = HarnessDecisionEngineResolver.Resolve(new() { ApiKey = "mine" }, Env(("SUBQ_API_KEY", "sq")));
        Assert.Equal(("api.typesafe.ai", "mine"), (configured.Host!, configured.ApiKey!));
    }

    [Theory]
    [InlineData("hello world", 2)]
    [InlineData("abcdefgh", 2)]
    [InlineData("1234", 2)]
    [InlineData("a,b", 3)]
    [InlineData("", 0)]
    public void TokenEstimateMatchesNur(string text, long expected) => Assert.Equal(expected, HarnessDecisionTokens.Estimate(text));

    [Fact]
    public async Task ClientSplitsQuestionsByCountAndTokenBudget()
    {
        var fake = new FakeEngine(AnswerAll(0.7));
        var client = new HarnessDecisionClient(new HttpClient(fake), new HarnessDecisionModelOptions().Normalize());
        var questions = Enumerable.Range(0, 30).Select(n => new HarnessDecisionQuestion($"q_{n}", "Does it matter?", "yes", "no")).ToArray();
        var answers = await client.AskAsync(Engine(), new JsonObject { ["context"] = "x" }, questions, default);
        Assert.Equal(2, answers.Requests);
        Assert.Equal(30, answers.Answers.Count);
        Assert.Equal([24, 6], fake.Bodies.Select(x => JsonNode.Parse(x)!["questions"]!.AsObject().Count).Order().Reverse());
        Assert.All(fake.Authorization, x => Assert.Equal("Bearer test-key", x));
        Assert.All(fake.Bodies, x => Assert.Equal("jev-latest", JsonNode.Parse(x)!["model"]!.GetValue<string>()));

        var budgeted = HarnessDecisionClient.Plan(900, questions, 24, 1000);
        Assert.True(budgeted.Count > 2);
        Assert.All(budgeted, chunk => Assert.True(900 + 20 + chunk.Sum(q => HarnessDecisionTokens.Estimate(q.Id +
            "{\"type\":\"noul\",\"instructions\":\"Does it matter?\",\"criteria\":{\"true\":\"yes\",\"false\":\"no\"}}")) <= 1000));
    }

    [Fact]
    public async Task OversizedStateFailsWithoutSending()
    {
        var fake = new FakeEngine(AnswerAll(0.7));
        var client = new HarnessDecisionClient(new HttpClient(fake), new HarnessDecisionModelOptions { MaxRequestTokens = 1000 }.Normalize());
        var state = new JsonObject { ["context"] = string.Join(' ', Enumerable.Repeat("word", 2000)) };
        await Assert.ThrowsAsync<HarnessDecisionException>(() => client.AskAsync(Engine(), state, [new("q_0", "?", "y", "n")], default));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task TransientFailuresRetryAndClientErrorsDoNot()
    {
        var options = new HarnessDecisionModelOptions().Normalize();
        var flaky = new FakeEngine((body, call) => call == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : AnswerAll(0.6)(body, call));
        var answers = await new HarnessDecisionClient(new HttpClient(flaky), options).AskAsync(Engine(), new JsonObject(), [new("q_0", "?", "y", "n")], default);
        Assert.Equal(2, flaky.Calls);
        Assert.Equal(0.6, answers.Answers["q_0"]);

        var rejected = new FakeEngine((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<HarnessDecisionException>(() => new HarnessDecisionClient(new HttpClient(rejected), options)
            .AskAsync(Engine(), new JsonObject(), [new("q_0", "?", "y", "n")], default));
        Assert.Equal(1, rejected.Calls);

        var down = new FakeEngine((_, _) => new HttpResponseMessage((HttpStatusCode)529));
        await Assert.ThrowsAsync<HarnessDecisionException>(() => new HarnessDecisionClient(new HttpClient(down), options)
            .AskAsync(Engine(), new JsonObject(), [new("q_0", "?", "y", "n")], default));
        Assert.Equal(3, down.Calls);

        var garbage = new FakeEngine((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });
        await Assert.ThrowsAsync<HarnessDecisionException>(() => new HarnessDecisionClient(new HttpClient(garbage), options)
            .AskAsync(Engine(), new JsonObject(), [new("q_0", "?", "y", "n")], default));
        Assert.Equal(1, garbage.Calls);
    }

    [Fact]
    public async Task MalformedAnswersBecomeMissing()
    {
        var fake = new FakeEngine((_, _) => Json(new { model = "jev-latest", answers = new Dictionary<string, object>
        {
            ["good"] = new { type = "noul", noul = 0.25 }, ["wrong_type"] = new { type = "score", noul = 0.5 },
            ["out_of_range"] = new { type = "noul", noul = 3 }, ["text"] = new { type = "noul", noul = "high" },
        }, usage = new { input_tokens = 11, output_tokens = 3 } }));
        var answers = await new HarnessDecisionClient(new HttpClient(fake), new HarnessDecisionModelOptions().Normalize())
            .AskAsync(Engine(), new JsonObject(), [new("good", "?", "y", "n"), new("wrong_type", "?", "y", "n")], default);
        Assert.Equal(["good"], answers.Answers.Keys);
        Assert.Equal((11, 3), (answers.InputTokens, answers.OutputTokens));
    }

    [Fact]
    public async Task CompactorKeepsTruncatesAndDropsWithoutTouchingPinnedOrText()
    {
        var fake = new FakeEngine(AnswerByTool(new() { ["keep"] = (0.9, 0.9), ["trim"] = (0.9, 0.1), ["drop"] = (0.1, 0.1) }));
        var compactor = Compactor(fake);
        var messages = Transcript(["keep", "trim", "drop", "mystery"], out var recent);
        var before = messages.Select(Snapshot).ToArray();

        var outcome = await compactor.CompactAsync(Guid.NewGuid(), messages, default);

        Assert.Null(outcome.Stats.Skipped);
        Assert.Equal((2, 1, 1), (outcome.Stats.Kept!.Value, outcome.Stats.Truncated!.Value, outcome.Stats.Dropped!.Value));
        Assert.Equal(1, outcome.Stats.Pinned); // the recent pair inside the preserved window
        Assert.All(outcome.Changes, x => Assert.Equal("decision", x.Method));
        var calls = messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>().Select(x => x.CallId).ToHashSet();
        var results = messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.DoesNotContain("call-drop", calls);
        Assert.All(results, x => Assert.Contains(x.CallId, calls)); // no orphan results
        Assert.Equal(before[0], Snapshot(messages[0])); // the first message is pinned
        Assert.Equal(recent, messages.TakeLast(recent.Length).Select(Snapshot)); // pinned window is untouched
        var trimmed = (string)results.Single(x => x.CallId == "call-trim").Result!;
        Assert.StartsWith(new string('t', 300), trimmed);
        Assert.Contains("chars dropped by decision-model compaction", trimmed);
        Assert.Equal(new string('k', 5000), HarnessDecisionCompactor.ResultText(results.Single(x => x.CallId == "call-keep").Result));
        Assert.Equal(new string('m', 5000), HarnessDecisionCompactor.ResultText(results.Single(x => x.CallId == "call-mystery").Result)); // missing answer keeps
        Assert.Contains(messages, x => x.Text == "Note about drop"); // text is never removed
        var state = JsonNode.Parse(fake.Bodies.Single())!["state"]!;
        Assert.Contains("[1] call-keep keep", state["context"]!["transcript"]!.GetValue<string>());
        Assert.Contains("Find the regression", state["context"]!["goal"]!.GetValue<string>());
    }

    [Fact]
    public async Task CachedDecisionsAvoidASecondRequest()
    {
        var fake = new FakeEngine(AnswerByTool(new() { ["keep"] = (0.9, 0.9), ["drop"] = (0.1, 0.1) }));
        var compactor = Compactor(fake);
        var installation = Guid.NewGuid();
        await compactor.CompactAsync(installation, Transcript(["keep", "drop"], out _), default);
        var again = Transcript(["keep", "drop"], out _);
        var outcome = await compactor.CompactAsync(installation, again, default);
        Assert.Equal(1, fake.Calls);
        Assert.Equal((2, 0, 1), (outcome.Stats.Cached!.Value, outcome.Stats.Requests!.Value, outcome.Stats.Dropped!.Value));
        Assert.DoesNotContain(again.SelectMany(x => x.Contents).OfType<FunctionCallContent>(), x => x.CallId == "call-drop");
        await compactor.CompactAsync(Guid.NewGuid(), Transcript(["keep", "drop"], out _), default);
        Assert.Equal(2, fake.Calls); // the cache is installation-scoped
    }

    [Fact]
    public async Task EngineFailureLeavesMessagesUnchanged()
    {
        var fake = new FakeEngine((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var messages = Transcript(["drop"], out _);
        var before = messages.Select(Snapshot).ToArray();
        var outcome = await Compactor(fake).CompactAsync(Guid.NewGuid(), messages, default);
        Assert.Contains("HTTP 401", outcome.Stats.Skipped);
        Assert.Empty(outcome.Changes);
        Assert.Equal(before, messages.Select(Snapshot));
    }

    [Fact]
    public async Task SecretShapedPreviewsAreRedacted()
    {
        var fake = new FakeEngine(AnswerAll(0.9));
        var messages = Transcript(["keep"], out _);
        messages[2].Contents[0] = new FunctionResultContent("call-keep", "Authorization: Bearer sk-live-abcdefghijklmnop " + new string('k', 500));
        await Compactor(fake).CompactAsync(Guid.NewGuid(), messages, default);
        var body = fake.Bodies.Single();
        Assert.DoesNotContain("sk-live-abcdefghijklmnop", body);
        Assert.Contains("[redacted: this looked like a credential and was not sent]", body);
    }

    [Theory]
    [InlineData(256_000, 217_601, true)]
    [InlineData(256_000, 217_600, false)]
    [InlineData(100_000, 83_617, true)]
    [InlineData(20_000, 17_001, true)] // 16,384 would take over half the window, so 15% applies
    [InlineData(0, 1_000_000, false)]
    public void PressureUsesNurReserve(long window, long tokens, bool expected) =>
        Assert.Equal(expected, HarnessDecisionCompactor.IsUnderPressure(tokens, window));

    internal static HarnessDecisionEngine Engine() => HarnessDecisionEngineResolver.Resolve(new() { ApiKey = "test-key" }, _ => null);

    internal static HarnessDecisionCompactor Compactor(FakeEngine fake)
    {
        var options = new HarnessDecisionModelOptions().Normalize();
        return new(new HarnessDecisionClient(new HttpClient(fake), options), Engine(), options, new HarnessDecisionCache());
    }

    private static string Snapshot(ChatMessage message) => message.Role.Value + ":" + string.Join("|", message.Contents.Select(x => x switch
    {
        TextContent text => "t:" + text.Text,
        FunctionCallContent call => "c:" + call.CallId,
        FunctionResultContent result => "r:" + result.CallId + ":" + HarnessDecisionCompactor.ResultText(result.Result),
        _ => x.GetType().Name,
    }));

    /// <summary>Goal, one call/result pair per tool, a note, then six recent messages including a pinned pair.</summary>
    private static List<ChatMessage> Transcript(string[] tools, out string[] recent)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Find the regression in the billing export") };
        foreach (var tool in tools)
        {
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"call-{tool}", tool, new Dictionary<string, object?> { ["path"] = $"/src/{tool}.cs" })]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"call-{tool}", new string(tool[0], 5000))]));
        }
        messages.Add(new(ChatRole.Assistant, "Note about drop"));
        messages.Add(new(ChatRole.User, "Keep going"));
        messages.Add(new(ChatRole.Assistant, [new FunctionCallContent("call-recent", "recent", new Dictionary<string, object?>())]));
        messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call-recent", new string('r', 5000))]));
        messages.Add(new(ChatRole.Assistant, "Looking at the export"));
        messages.Add(new(ChatRole.User, "Thanks"));
        messages.Add(new(ChatRole.Assistant, "Almost done"));
        recent = messages.TakeLast(6).Select(Snapshot).ToArray();
        return messages;
    }

    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    internal static Func<JsonNode, int, HttpResponseMessage> AnswerAll(double p) => (body, _) => Json(new
    {
        model = "jev-latest",
        answers = body["questions"]!.AsObject().ToDictionary(x => x.Key, _ => (object)new { type = "noul", noul = p }),
        usage = new { input_tokens = 10, output_tokens = 2 },
    });

    /// <summary>Answers by the tool named in <c>state.calls[n]</c>; an unknown tool gets no answer.</summary>
    internal static Func<JsonNode, int, HttpResponseMessage> AnswerByTool(Dictionary<string, (double Call, double Result)> byTool) => (body, _) =>
    {
        var calls = body["state"]!["calls"]!.AsArray();
        var answers = new Dictionary<string, object>();
        foreach (var question in body["questions"]!.AsObject())
        {
            var n = int.Parse(question.Key[(question.Key.LastIndexOf('_') + 1)..]);
            if (!byTool.TryGetValue(calls[n]!["tool"]!.GetValue<string>(), out var p)) continue;
            answers[question.Key] = new { type = "noul", noul = question.Key.StartsWith("keep_call_") ? p.Call : p.Result };
        }
        return Json(new { model = "jev-latest", answers, usage = new { input_tokens = 100, output_tokens = 4 } });
    };

    internal sealed class FakeEngine(Func<JsonNode, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public List<string> Bodies { get; } = [];
        public List<string?> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies) { Bodies.Add(body); Authorization.Add(request.Headers.Authorization?.ToString()); }
            return respond(JsonNode.Parse(body)!, call);
        }
    }
}
