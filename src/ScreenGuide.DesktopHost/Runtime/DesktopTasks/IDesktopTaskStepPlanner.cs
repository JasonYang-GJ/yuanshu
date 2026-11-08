using ScreenGuide.AI.Core;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public interface IDesktopTaskStepPlanner
{
    Task<DesktopTaskProposal> PlanAsync(
        Guid turnId,
        FrozenChatModelRoute route,
        string instruction,
        DesktopTaskSnapshot snapshot,
        IReadOnlyList<string> history,
        CancellationToken cancellationToken = default);
}
