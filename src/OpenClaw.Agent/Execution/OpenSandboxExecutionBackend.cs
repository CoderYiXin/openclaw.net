using System.Diagnostics;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal sealed class OpenSandboxExecutionBackend : IExecutionBackend
{
    private readonly string _name;
    private readonly IToolSandbox _toolSandbox;
    private readonly int _timeoutSeconds;

    public OpenSandboxExecutionBackend(string name, IToolSandbox toolSandbox, int timeoutSeconds = 30)
    {
        _name = name;
        _toolSandbox = toolSandbox;
        _timeoutSeconds = timeoutSeconds;
    }

    public string Name => _name;

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            var result = await _toolSandbox.ExecuteAsync(new SandboxExecutionRequest
            {
                Command = request.Command,
                Arguments = request.Arguments,
                LeaseKey = request.LeaseKey,
                Environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal),
                WorkingDirectory = request.WorkingDirectory,
                Template = request.Template,
                TimeToLiveSeconds = request.TimeToLiveSeconds
            }, timeoutCts.Token);

            return new ExecutionResult
            {
                BackendName = _name,
                ExitCode = result.ExitCode,
                Stdout = result.Stdout,
                Stderr = result.Stderr,
                TimedOut = false,
                FallbackUsed = false,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ExecutionResult
            {
                BackendName = _name,
                ExitCode = -1,
                Stdout = string.Empty,
                Stderr = string.Empty,
                TimedOut = true,
                FallbackUsed = false,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }
}
