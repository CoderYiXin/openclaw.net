using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IExecutionBackend
{
    string Name { get; }

    Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default);
}
