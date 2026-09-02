namespace OpenClaw.Core.Abstractions;

/// <summary>
/// A tool that the agent can invoke. Kept minimal for AOT trimming.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    
    /// <summary>JSON schema describing the tool's parameters.</summary>
    string ParameterSchema { get; }
    
    /// <summary>Execute the tool with the given JSON arguments.</summary>
    ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct);
}

/// <summary>
/// Optional structured output contract for tools that return stable JSON values.
/// </summary>
public interface IToolOutputSchema
{
    /// <summary>JSON schema describing the tool's structured result.</summary>
    string? OutputSchema { get; }
}
