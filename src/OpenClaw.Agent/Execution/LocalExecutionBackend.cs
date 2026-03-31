using System.Diagnostics;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal sealed class LocalExecutionBackend : ProcessExecutionBackendBase
{
    private readonly ExecutionBackendProfileConfig _profile;

    public LocalExecutionBackend(ExecutionBackendProfileConfig profile)
    {
        _profile = profile;
    }

    public override string Name => "local";

    public override ExecutionBackendCapabilities Capabilities { get; } = new()
    {
        SupportsOneShotCommands = true,
        SupportsProcesses = true,
        SupportsPty = !OperatingSystem.IsWindows(),
        SupportsInteractiveInput = true
    };

    public override Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        => ExecuteProcessAsync(Name, CreateProcessStartInfo(request), _profile.TimeoutSeconds, cancellationToken);

    protected override ProcessStartInfo CreateProcessStartInfo(ExecutionRequest request)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory ?? _profile.WorkingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in _profile.Environment)
            psi.Environment[key] = value;
        foreach (var (key, value) in request.Environment)
            psi.Environment[key] = value;

        return psi;
    }
}
