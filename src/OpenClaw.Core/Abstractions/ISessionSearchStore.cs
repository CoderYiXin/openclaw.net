using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ISessionSearchStore
{
    ValueTask<SessionSearchResult> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct);
}
