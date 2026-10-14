using CSweet.Contracts.PersonalAssistant;

namespace CSweet.Infrastructure.PersonalAssistant;

public static class RepositoryBriefValidator
{
    public static void ValidateReview(RepositoryBusinessReview review)
    {
        if (review is null || string.IsNullOrWhiteSpace(review.BusinessName) || review.BusinessName.Length > 256 ||
            review.Industry is null || review.Industry.Length > 160 || review.Mission is null || review.Mission.Length > 4096 ||
            review.NextGoal is null || review.NextGoal.Length > 4096 || review.Summary is null || review.Summary.Length > 16000 ||
            review.Projects is null || review.Projects.Count > 50 || review.OpenQuestions is null ||
            review.OpenQuestions.Count > 20 || review.OpenQuestions.Any(x => x is null || x.Length > 2048))
            throw new ArgumentException("Review the business name and keep the company and project brief within the displayed limits.");
        foreach (var project in review.Projects)
            if (project is null || project.SourceId is null || project.Name is null || project.Name.Length > 256 ||
                project.Summary is null || project.Summary.Length > 6000 || project.Technology is null || project.Technology.Length > 512 ||
                project.CurrentState is null || project.CurrentState.Length > 2048 || project.Evidence is null || project.Evidence.Count > 80)
                throw new ArgumentException("One of the project summaries is invalid or too large.");
    }

    public static void ValidateEvidence(IReadOnlyList<RepositoryProjectBrief> projects, IReadOnlyList<RepositoryAnalysisInput> inputs)
    {
        if (projects is null || projects.Any(x => x is null || x.Evidence is null) || projects.Count != inputs.Count || projects.Select(x => x.SourceId).Distinct().Count() != inputs.Count ||
            projects.Any(project => !inputs.Any(input => input.SourceId == project.SourceId) ||
                project.Evidence.Any(e => e is null || e.SourceId != project.SourceId || !inputs.Any(input =>
                    input.SourceId == e.SourceId && input.Snapshot == e.Snapshot && input.Files.Any(file => file.Path == e.Path)))))
            throw new ArgumentException("The brief's evidence no longer matches the selected repository snapshots.");
    }
}
