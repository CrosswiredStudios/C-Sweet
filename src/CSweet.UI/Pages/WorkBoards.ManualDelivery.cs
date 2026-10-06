using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Pages;

public partial class WorkBoards
{
    private bool _manualDeliveryOpen, _manualBusy;
    private WorkStageExecutionResponse? _manualDeliveryStage;
    private WorkBoardItemResponse? _manualDeliveryItem;
    private ArtifactRevisionResponse? _manualDocument;
    private List<ManualCriterion> _manualCriteria = [];
    private string _manualCommit = "", _manualTarget = "", _manualSummary = "", _manualFindings = "", _manualCommand = "", _manualTestOutput = "";
    private string? _manualError;
    private int _manualExitCode;
    private string _manualDecisionKey = "";
    private string _manualArtifactId = "", _manualArtifactRevisionId = "";
    private sealed class ManualCriterion(string criterion)
    {
        public string Criterion { get; } = criterion;
        public bool Satisfied { get; set; }
        public string Evidence { get; set; } = "";
    }
    private async Task OpenManualDeliveryAsync(WorkBoardItemResponse item, WorkItemExecutionResponse execution, WorkStageExecutionResponse stage)
    {
        _manualDeliveryStage = stage; _manualDeliveryItem = item; _manualDeliveryOpen = true;
        _manualDocument = null; _manualError = null; _manualCommit = ""; _manualTarget = "";
        _manualSummary = ""; _manualFindings = ""; _manualCommand = ""; _manualTestOutput = ""; _manualExitCode = 0;
        _manualDecisionKey = Guid.NewGuid().ToString("N");
        _manualArtifactId = ""; _manualArtifactRevisionId = "";
        _manualCriteria = item.Delivery!.AcceptanceCriteria.Select(x => new ManualCriterion(x)).ToList();
        var outcomes = execution.Stages.Where(x => x.Traversal == stage.Traversal && x.LatestOutcome is not null).Select(x => x.LatestOutcome!).ToArray();
        string? Property(string name) => outcomes.Select(x => x.Output).Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => x.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value)
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).LastOrDefault(x => x is not null);
        if (item.Delivery.DeliveryKind == "Code")
        {
            _manualCommit = Property(stage.StageKey == "quality" ? "mergeCommitSha" : "commitSha") ?? "";
            return;
        }
        if (stage.StageKey is "development" or "specialist-execution") return;
        try
        {
            if (!Guid.TryParse(Property("artifactId"), out var artifact) || !Guid.TryParse(Property("revisionId"), out var revision))
                throw new InvalidOperationException("The author has not delivered an identifiable artifact revision.");
            var detail = await Http.GetFromJsonAsync<ArtifactDocumentDetail>($"api/organizations/{OrganizationId:D}/documents/{artifact:D}");
            _manualDocument = detail?.Revisions.SingleOrDefault(x => x.Id == revision);
            if (_manualDocument is null || _manualDocument.ContentSha256 != Property("sha256"))
                throw new InvalidOperationException("The delivered revision is unavailable or differs from the author’s digest.");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { _manualError = error.Message; }
    }
    private async Task LoadManualArtifactAsync()
    {
        _manualError = null; _manualDocument = null;
        try
        {
            if (!Guid.TryParse(_manualArtifactId, out var artifact) || !Guid.TryParse(_manualArtifactRevisionId, out var revision))
                throw new InvalidOperationException("Provide the document and revision identities.");
            var detail = await Http.GetFromJsonAsync<ArtifactDocumentDetail>($"api/organizations/{OrganizationId:D}/documents/{artifact:D}");
            _manualDocument = detail?.Revisions.SingleOrDefault(x => x.Id == revision) ?? throw new InvalidOperationException("The delivered revision is unavailable.");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { _manualError = error.Message; }
    }
    private async Task SubmitManualDeliveryAsync(bool approved)
    {
        if (_manualBusy || _manualDeliveryStage is not { } stage || _manualDeliveryItem?.Delivery is not { } delivery || _execution is null || _detail is null) return;
        _manualBusy = true; _manualError = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_manualSummary)) throw new InvalidOperationException("Describe the actual review evidence.");
            var criteria = _manualCriteria.Select(x => new WorkDeliveryCriterionResult(x.Criterion, x.Satisfied, x.Evidence)).ToArray();
            var findings = _manualFindings.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!approved && findings.Length == 0) throw new InvalidOperationException("Identify the findings requiring fixes.");
            if (stage.StageKey == "quality" && (criteria.Any(x => string.IsNullOrWhiteSpace(x.Evidence)) || approved && criteria.Any(x => !x.Satisfied)))
                throw new InvalidOperationException("Record evidence for every criterion before submitting QA.");
            object output;
            List<WorkExecutionEvidence> evidence = [];
            if (delivery.DeliveryKind == "Artifact")
            {
                var document = _manualDocument ?? throw new InvalidOperationException("Read the exact delivered document before submitting QA.");
                output = stage.StageKey == "quality"
                    ? new WorkArtifactQualityResult(document.ArtifactId, document.Id, document.ContentSha256, approved, _manualSummary, criteria, findings)
                    : new { artifactId = document.ArtifactId, revisionId = document.Id, sha256 = document.ContentSha256 };
                evidence.Add(new("artifact-revision", document.Id.ToString("D"), document.ContentSha256));
            }
            else
            {
                evidence.Add(new("commit", "Exact reviewed/tested commit", _manualCommit));
                if (stage.StageKey == "technical-review")
                {
                    evidence.Add(new("target-commit", "Reviewed story target", _manualTarget));
                    output = new { };
                }
                else output = new WorkDeliveryReviewResult(_manualCommit, approved, _manualSummary, criteria, findings)
                { Validations = [new(delivery.RepositoryId, _manualCommit, _manualCommand, _manualExitCode, _manualExitCode == 0, _manualTestOutput)] };
            }
            var outcome = stage.StageKey == "technical-review" ? (approved ? "approved" : "rejected") :
                stage.StageKey == "quality" ? (approved ? "passed" : "failed") : "artifact-delivered";
            using var response = await Http.PostAsJsonAsync($"api/organizations/{OrganizationId:D}/work/boards/{_detail.Board.Id:D}/orchestration/stages/{stage.Id:D}/manual-completion",
                new CompleteManualWorkStageRequest(_detail.Board.Id, _execution.Id, stage.Id, outcome, _manualSummary,
                    JsonSerializer.SerializeToElement(output, new JsonSerializerOptions(JsonSerializerDefaults.Web)), _manualDecisionKey) { Evidence = evidence });
            if (!response.IsSuccessStatusCode) { _manualError = await ApiErrorAsync(response); return; }
            _manualDeliveryOpen = false;
            await ReloadExecutionAsync();
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { _manualError = error.Message; }
        finally { _manualBusy = false; }
    }
}
