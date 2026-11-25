extern alias Developer;
extern alias Engineer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Infrastructure.Llm;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using OpenAI;
using GameEngineer = Engineer::CSweet.Agent.Engineer.VideoGame.GameEngineerAgent;
using SoftwareDeveloper = Developer::CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent;

namespace CSweet.GameRoleWorkflow.Tests;

[CollectionDefinition("DeepSeek assigned workspace", DisableParallelization = true)]
public sealed class DeepSeekAssignedWorkspaceCollection;

[Collection("DeepSeek assigned workspace")]
public sealed class DeepSeekExecutionJourneyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string VerifyCanvas = "node verify.cjs";

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prism_Breaker_execution_keeps_DeepSeek_reasoning_through_tool_history_compaction(bool useSoftwareDeveloper)
    {
        const string sourceFile = "index.html";
        var workspace = Guid.NewGuid();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspace);
        var item = Empty<WorkItem>() with
        {
            Id = Guid.NewGuid(), Title = "Runnable slice with a fixed Canvas 2D frame",
            Development = new(Guid.NewGuid(), "linux", ["Preserve the canvas element"], [])
        };
        var assignment = Empty<WorkExecutionAssignment>() with
        {
            Scope = "Task", SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(),
            StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BoardId = Guid.NewGuid(), ItemId = item.Id,
            AssignmentRevision = 1, Evidence = [], PriorOutcomes = [], Item = JsonSerializer.SerializeToElement(new { }),
            Input = JsonSerializer.SerializeToElement(new { allowedOutcomeCodes = new[] { "code-published", "blocked" } })
        };
        using var source = new MemoryStream();
        using (var zip = new ZipArchive(source, ZipArchiveMode.Create, true))
        {
            for (var i = 0; i < 5; i++)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"old-{i}.txt").Open());
                await writer.WriteAsync(string.Concat(Enumerable.Range(0, 1200).Select(n => $"Record {i}-{n} from the workspace.\n")));
            }
            using var validation = new StreamWriter(zip.CreateEntry("verify.cjs").Open());
            await validation.WriteAsync("if (!require('node:fs').readFileSync('index.html', 'utf8').includes('<canvas>')) process.exit(1);");
        }

        var publications = new List<PublishGitWorkspaceRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<TeamRosterRequest, TeamRosterResponse>(PlatformCapabilities.TeamRosterRead, (_, _) =>
                Task.FromResult(new TeamRosterResponse(new AgentTeamContext("fixture", "Team", "Team", 1, "manager", "Producer", [], [], 1, false))))
            .RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) => Task.FromResult(item))
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkManagementCapabilityNames.BoardRead,
                (_, _) => Task.FromResult(new WorkBoardDetail(Empty<WorkBoardSummary>(), [], [])))
            .RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkManagementCapabilityNames.ItemCommentsRead,
                (_, _) => Task.FromResult(new WorkItemCommentPage([], 1, 20, false, 1)))
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, (_, _) => Task.FromResult(
                new GitWorkspaceResult(workspace, item.Id, "/remote", item.Development.RepositoryId, "InternalGit", "BranchOnly", new string('a', 40), "Ready", false)))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (r, _) =>
                Task.FromResult(r.Direction == "pull" ? new GitWorkspaceSyncResult(source.ToArray()) :
                    new GitWorkspaceSyncResult { Checkpoint = new(workspace, 1, Convert.ToHexStringLower(SHA256.HashData(r.Archive!))) }))
            .RegisterCapability<InspectGitWorkspaceRequest, GitWorkspaceInspection>(GitWorkspaceCapabilities.Inspect,
                (_, _) => Task.FromResult(new GitWorkspaceInspection(workspace, "Modified", true, [sourceFile], [])))
            .RegisterCapability<PublishGitWorkspaceRequest, GitWorkspacePublication>(GitWorkspaceCapabilities.Publish, (r, _) =>
            {
                publications.Add(r);
                return Task.FromResult(new GitWorkspacePublication(Guid.NewGuid(), workspace, item.Development.RepositoryId, "InternalGit",
                    "BranchOnly", "work/fixture", new string('b', 40), null, "Published"));
            })
            .RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(WorkManagementCapabilityNames.ItemComment, (_, _) => Task.FromResult(Empty<WorkItemComment>()))
            .RegisterCapability<CleanupGitWorkspaceRequest, GitWorkspaceCleanupResult>(GitWorkspaceCapabilities.Cleanup,
                (_, _) => Task.FromResult(new GitWorkspaceCleanupResult(workspace, false, null)));
        _ = new OperatingStateFixture(runtime);

        using var endpoint = new DeepSeekStream(sourceFile);
        using var http = new HttpClient(endpoint);
        var sdk = new OpenAI.Chat.ChatClient("deepseek-flash", new ApiKeyCredential("fixture"),
            new OpenAIClientOptions { Endpoint = new Uri("https://provider.invalid/v1/"), Transport = new HttpClientPipelineTransport(http) });
        var installation = Guid.NewGuid();
        var chatFactory = new ChatFactory(sdk);
        CSweetAgentBase developer = useSoftwareDeveloper ? new SoftwareDeveloper(chatFactory) : new GameEngineer(chatFactory);
        var host = new AgentTestHost(developer, runtime, installationId: installation.ToString());
        var settings = new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
            ["llmModel"] = JsonSerializer.SerializeToElement("deepseek-flash"),
            ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(20_000),
            ["maxOutputTokens"] = JsonSerializer.SerializeToElement(12_000)
        };
        var limits = new Dictionary<string, string>
        {
            ["CSWEET_WORKSPACE_MAXIMUM_ARCHIVE_BYTES"] = "8388608",
            ["CSWEET_WORKSPACE_MAXIMUM_EXPANDED_BYTES"] = "268435456",
            ["CSWEET_WORKSPACE_MAXIMUM_FILE_COUNT"] = "20000"
        };
        var previous = limits.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in limits) Environment.SetEnvironmentVariable(key, value);
            Assert.True((await host.ApplyConfigurationAsync(new(installation, "1.0", settings, settings.Keys.ToArray(), 1, "fixture"))).Succeeded);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await host.ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun, assignment, cancellationToken: deadline.Token);
            Assert.True(endpoint.Rejection is null, endpoint.Rejection);
            Assert.True(result.Succeeded, result.Error);
            var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Web)!;
            Assert.True(outcome.Disposition == WorkExecutionDispositions.Completed, outcome.Summary);
            var publication = Assert.Single(publications);
            Assert.Equal(workspace, publication.WorkspaceId);
            var validation = Assert.Single(publication.Validations!);
            Assert.True(validation.Command == VerifyCanvas && validation.Succeeded && validation.ExitCode == 0);
            Assert.True(endpoint.Compacted, "The test must exercise the request after tool history compaction.");
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}", Web)!;

    private sealed class ChatFactory(OpenAI.Chat.ChatClient client) : IAgentLlmClientFactory
    {
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(new StreamedChatClient(OpenAiCompatibleLlmProviderFactory.AdaptChatClient(client)));
    }

    // The installed agent's platform client uses streaming even when the harness requests a complete response.
    private sealed class StreamedChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
    }

    private sealed class DeepSeekStream(string sourceFile) : HttpMessageHandler
    {
        private int requests;
        public string? Rejection { get; private set; }
        public bool Compacted { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var toolCalls = new List<string>();
            foreach (var message in messages.Where(message => message.GetProperty("role").GetString() == "assistant"))
            {
                if (!message.TryGetProperty("reasoning_content", out var reasoning))
                {
                    var summary = message.TryGetProperty("content", out var content) && content.ToString().StartsWith("[Tool Calls]", StringComparison.Ordinal);
                    Rejection = $"Request {requests + 1} sent {(summary ? "a compacted tool summary" : "an assistant message")} without reasoning_content.";
                    return new(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":{"message":"The `reasoning_content` in the thinking mode must be passed back to the API.","type":"invalid_request_error","code":"invalid_request_error"}}""", Encoding.UTF8, "application/json")
                    };
                }
                if (message.TryGetProperty("tool_calls", out var calls))
                {
                    var callId = Assert.Single(calls.EnumerateArray()).GetProperty("id").GetString()!;
                    Assert.Equal($"Inspect the workspace, step {callId}.", reasoning.GetString());
                    toolCalls.Add(callId);
                }
            }
            Assert.Equal(Enumerable.Range(1, requests).Select(step => $"call-{step}"), toolCalls);
            var toolResults = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            Assert.Equal(toolCalls, toolResults.Select(message => message.GetProperty("tool_call_id").GetString()));
            Compacted |= toolResults.Any(message =>
                message.GetProperty("content").ToString().Contains("Older tool detail abbreviated", StringComparison.Ordinal));
            requests++;

            return requests switch
            {
                1 => Tool("record_execution_plan", new { steps = new[] {
                    new { requirement = "Inspect the source", verification = "Read the workspace records" },
                    new { requirement = "Preserve the canvas", verification = VerifyCanvas },
                    new { requirement = "Publish the slice", verification = "Record the result" } } }),
                >= 2 and <= 6 => Tool("file_access_read", new { fileName = $"old-{requests - 2}.txt" }),
                7 => Tool("file_access_write", new { fileName = sourceFile, content = "<canvas></canvas>", overwrite = true }),
                8 => Tool("execute_workspace_command", new { command = VerifyCanvas }),
                9 => Tool("file_access_write", new { fileName = ".csweet/outcome.json", overwrite = true, content = JsonSerializer.Serialize(new
                    { summary = "Canvas slice complete", changedFiles = new[] { sourceFile }, validations = new[] { new { command = VerifyCanvas, succeeded = true, exitCode = 0 } } }) }),
                10 => Stream(new { content = "Canvas slice complete" }, "stop"),
                _ => throw new InvalidOperationException($"Unexpected model request {requests}.")
            };
        }

        private HttpResponseMessage Tool(string name, object arguments) => Stream(new
        {
            tool_calls = new[] { new { index = 0, id = $"call-{requests}", type = "function",
                function = new { name, arguments = JsonSerializer.Serialize(arguments) } } }
        }, "tool_calls");

        private HttpResponseMessage Stream(object delta, string finishReason)
        {
            string Frame(object update, string? finish = null) => "data: " + JsonSerializer.Serialize(new
            {
                id = $"response-{requests}", @object = "chat.completion.chunk", created = 1, model = "deepseek-flash",
                choices = new[] { new { index = 0, delta = update, finish_reason = finish } }
            }) + "\n\n";

            var stream = Frame(new { role = "assistant", reasoning_content = "Inspect the workspace, " }) +
                Frame(new { reasoning_content = $"step call-{requests}." }) + Frame(delta, finishReason) + "data: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
