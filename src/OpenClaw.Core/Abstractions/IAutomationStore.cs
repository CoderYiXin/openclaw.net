using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IAutomationStore
{
    ValueTask<IReadOnlyList<AutomationDefinition>> ListAutomationsAsync(CancellationToken ct);
    ValueTask<AutomationDefinition?> GetAutomationAsync(string automationId, CancellationToken ct);
    ValueTask SaveAutomationAsync(AutomationDefinition automation, CancellationToken ct);
    ValueTask DeleteAutomationAsync(string automationId, CancellationToken ct);
    ValueTask<AutomationRunState?> GetRunStateAsync(string automationId, CancellationToken ct);
    ValueTask SaveRunStateAsync(AutomationRunState runState, CancellationToken ct);
}
