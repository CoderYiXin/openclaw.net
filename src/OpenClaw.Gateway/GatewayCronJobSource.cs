using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway;

internal sealed class GatewayCronJobSource : ICronJobSource
{
    private readonly GatewayAutomationService _automationService;

    public GatewayCronJobSource(GatewayAutomationService automationService)
        => _automationService = automationService;

    public IReadOnlyList<CronJobConfig> GetJobs() => _automationService.BuildCronJobs();
}
