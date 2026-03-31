using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IToolPresetResolver
{
    ResolvedToolPreset Resolve(Session session, IEnumerable<string> availableToolNames);
    IReadOnlyList<ResolvedToolPreset> ListPresets(IEnumerable<string> availableToolNames);
}
