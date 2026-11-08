namespace ScreenGuide.Skills.Abstractions;

public enum ApplicationSearchStage { Opening, Opened, ResolvingMusic, PreparingSearch, Searching, VerifyingResults, ResolvingHotMusic, PlayingMusic }

/// <summary>Observed execution phase only. Never conveys target authority or a new task.</summary>
public sealed record ApplicationSearchProgress(ApplicationSearchStage Stage, string ApplicationName);

public interface IApplicationSearchProgressSink
{
    Task ReportAsync(ApplicationSearchProgress progress, CancellationToken cancellationToken = default);
}
