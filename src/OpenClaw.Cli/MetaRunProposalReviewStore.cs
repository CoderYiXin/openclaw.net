using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal sealed class MetaRunProposalReviewStore
{
    private readonly string _reviewsPath;

    public MetaRunProposalReviewStore(string memoryPath)
    {
        _reviewsPath = Path.Combine(Path.GetFullPath(memoryPath), "meta-run-proposal-reviews");
        Directory.CreateDirectory(_reviewsPath);
    }

    public async ValueTask<IReadOnlyDictionary<string, MetaRunProposalReviewRecord>> LoadBySessionAsync(string sessionId, CancellationToken ct)
    {
        var records = await LoadSessionRecordsAsync(sessionId, ct);
        return records.ToDictionary(static record => record.ProposalId, StringComparer.Ordinal);
    }

    public async ValueTask<MetaRunProposalReviewRecord?> GetAsync(string sessionId, string proposalId, CancellationToken ct)
    {
        var records = await LoadSessionRecordsAsync(sessionId, ct);
        return records.FirstOrDefault(record => string.Equals(record.ProposalId, proposalId, StringComparison.Ordinal));
    }

    public async ValueTask SaveAsync(MetaRunProposalReviewRecord record, CancellationToken ct)
    {
        var records = await LoadSessionRecordsAsync(record.SessionId, ct);
        var existingIndex = records.FindIndex(item => string.Equals(item.ProposalId, record.ProposalId, StringComparison.Ordinal));
        if (existingIndex >= 0)
            records[existingIndex] = record;
        else
            records.Add(record);

        await SaveSessionRecordsAsync(record.SessionId, records, ct);
    }

    private async ValueTask<List<MetaRunProposalReviewRecord>> LoadSessionRecordsAsync(string sessionId, CancellationToken ct)
    {
        var path = GetSessionPath(sessionId);
        if (!File.Exists(path))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.MetaRunProposalReviewRecordArray)?.ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async ValueTask SaveSessionRecordsAsync(string sessionId, List<MetaRunProposalReviewRecord> records, CancellationToken ct)
    {
        var path = GetSessionPath(sessionId);
        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(records.ToArray(), CoreJsonContext.Default.MetaRunProposalReviewRecordArray);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    private string GetSessionPath(string sessionId)
        => Path.Combine(_reviewsPath, $"{EncodeKey(sessionId)}.json");

    private static string EncodeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "item";

        var bytes = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
