using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class SqliteFeatureStore : IAutomationStore, IUserProfileStore, ILearningProposalStore, IDisposable
{
    private readonly string _dbPath;

    public SqliteFeatureStore(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        Initialize();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Cache = SqliteCacheMode.Shared,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    private void Initialize()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS automations (
              id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS automation_runs (
              automation_id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS user_profiles (
              actor_id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS learning_proposals (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              status TEXT NOT NULL,
              json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_learning_status ON learning_proposals(status, kind, updated_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask<IReadOnlyList<AutomationDefinition>> ListAutomationsAsync(CancellationToken ct)
        => await QueryJsonListAsync("SELECT json FROM automations ORDER BY updated_at DESC;", CoreJsonContext.Default.AutomationDefinition, ct);

    public ValueTask<AutomationDefinition?> GetAutomationAsync(string automationId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM automations WHERE id = $id LIMIT 1;", "$id", automationId, CoreJsonContext.Default.AutomationDefinition, ct);

    public ValueTask SaveAutomationAsync(AutomationDefinition automation, CancellationToken ct)
        => UpsertAsync(
            "INSERT INTO automations(id, json, updated_at) VALUES($id, $json, $updated_at) ON CONFLICT(id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;",
            "$id", automation.Id, JsonSerializer.Serialize(automation, CoreJsonContext.Default.AutomationDefinition), ct);

    public ValueTask DeleteAutomationAsync(string automationId, CancellationToken ct)
        => ExecuteAsync("DELETE FROM automations WHERE id = $id;", "$id", automationId, ct);

    public ValueTask<AutomationRunState?> GetRunStateAsync(string automationId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM automation_runs WHERE automation_id = $id LIMIT 1;", "$id", automationId, CoreJsonContext.Default.AutomationRunState, ct);

    public ValueTask SaveRunStateAsync(AutomationRunState runState, CancellationToken ct)
        => UpsertAsync(
            "INSERT INTO automation_runs(automation_id, json, updated_at) VALUES($id, $json, $updated_at) ON CONFLICT(automation_id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;",
            "$id", runState.AutomationId, JsonSerializer.Serialize(runState, CoreJsonContext.Default.AutomationRunState), ct);

    public async ValueTask<IReadOnlyList<UserProfile>> ListProfilesAsync(CancellationToken ct)
        => await QueryJsonListAsync("SELECT json FROM user_profiles ORDER BY updated_at DESC;", CoreJsonContext.Default.UserProfile, ct);

    public ValueTask<UserProfile?> GetProfileAsync(string actorId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM user_profiles WHERE actor_id = $id LIMIT 1;", "$id", actorId, CoreJsonContext.Default.UserProfile, ct);

    public ValueTask SaveProfileAsync(UserProfile profile, CancellationToken ct)
        => UpsertAsync(
            "INSERT INTO user_profiles(actor_id, json, updated_at) VALUES($id, $json, $updated_at) ON CONFLICT(actor_id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;",
            "$id", profile.ActorId, JsonSerializer.Serialize(profile, CoreJsonContext.Default.UserProfile), ct);

    public async ValueTask<IReadOnlyList<LearningProposal>> ListProposalsAsync(string? status, string? kind, CancellationToken ct)
    {
        var where = new List<string>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("status = $status");
            cmd.Parameters.AddWithValue("$status", status);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            where.Add("kind = $kind");
            cmd.Parameters.AddWithValue("$kind", kind);
        }

        cmd.CommandText = $"SELECT json FROM learning_proposals{(where.Count == 0 ? "" : $" WHERE {string.Join(" AND ", where)}")} ORDER BY updated_at DESC;";
        var results = new List<LearningProposal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var proposal = JsonSerializer.Deserialize(reader.GetString(0), CoreJsonContext.Default.LearningProposal);
            if (proposal is not null)
                results.Add(proposal);
        }

        return results;
    }

    public ValueTask<LearningProposal?> GetProposalAsync(string proposalId, CancellationToken ct)
        => QuerySingleAsync("SELECT json FROM learning_proposals WHERE id = $id LIMIT 1;", "$id", proposalId, CoreJsonContext.Default.LearningProposal, ct);

    public ValueTask SaveProposalAsync(LearningProposal proposal, CancellationToken ct)
        => UpsertLearningProposalAsync(proposal, ct);

    private async ValueTask UpsertAsync(string sql, string idParamName, string id, string json, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(idParamName, id);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask UpsertLearningProposalAsync(LearningProposal proposal, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learning_proposals(id, kind, status, json, updated_at)
            VALUES($id, $kind, $status, $json, $updated_at)
            ON CONFLICT(id) DO UPDATE SET kind=excluded.kind, status=excluded.status, json=excluded.json, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", proposal.Id);
        cmd.Parameters.AddWithValue("$kind", proposal.Kind);
        cmd.Parameters.AddWithValue("$status", proposal.Status);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(proposal, CoreJsonContext.Default.LearningProposal));
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask ExecuteAsync(string sql, string idParamName, string id, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(idParamName, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async ValueTask<T?> QuerySingleAsync<T>(
        string sql,
        string idParamName,
        string id,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(idParamName, id);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    private async ValueTask<IReadOnlyList<T>> QueryJsonListAsync<T>(
        string sql,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var results = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize(reader.GetString(0), typeInfo);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    public void Dispose()
    {
    }
}
