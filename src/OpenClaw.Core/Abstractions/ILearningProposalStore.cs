using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ILearningProposalStore
{
    ValueTask<IReadOnlyList<LearningProposal>> ListProposalsAsync(
        string? status,
        string? kind,
        CancellationToken ct);

    ValueTask<LearningProposal?> GetProposalAsync(string proposalId, CancellationToken ct);
    ValueTask SaveProposalAsync(LearningProposal proposal, CancellationToken ct);
}
