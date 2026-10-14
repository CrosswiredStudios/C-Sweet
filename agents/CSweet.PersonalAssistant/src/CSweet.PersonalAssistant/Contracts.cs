namespace CSweet.PersonalAssistant;

public sealed record AssistantMessage(string Role, string Content);
public sealed record ConversationRequest(IReadOnlyList<AssistantMessage> Messages, string? Context = null);
public sealed record ConversationResponse(string Message);
public sealed record SourceFile(string Path, string Content);
public sealed record RepositoryInput(string SourceId, string Name, string Snapshot,
    IReadOnlyList<SourceFile> Files, IReadOnlyList<string> Warnings);
public sealed record RepositoryAnalysisRequest(IReadOnlyList<RepositoryInput> Repositories);
public sealed record FileEvidence(string SourceId, string Path, string Snapshot);
public sealed record ProjectBrief(string SourceId, string Name, string Summary, string Technology,
    string CurrentState, IReadOnlyList<FileEvidence> Evidence);
public sealed record CompanyBrief(string BusinessName, string Industry, string Mission, string Summary,
    IReadOnlyList<ProjectBrief> Projects, IReadOnlyList<string> OpenQuestions);
