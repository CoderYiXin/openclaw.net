using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal sealed class TodoTool : IToolWithContext
{
    private readonly SessionMetadataStore _metadataStore;

    public TodoTool(SessionMetadataStore metadataStore)
    {
        _metadataStore = metadataStore;
    }

    public string Name => "todo";
    public string Description => "Manage a session-scoped todo list. Supports list, add, update, complete, remove, and clear.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["list","add","update","complete","remove","clear"],"default":"list"},
        "id":{"type":"string"},
        "text":{"type":"string"},
        "notes":{"type":"string"}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: todo requires execution context.");

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var action = GetString(root, "action") ?? "list";

        var metadata = _metadataStore.Get(context.Session.Id);
        var todos = metadata.TodoItems.ToList();

        switch (action)
        {
            case "list":
                return ValueTask.FromResult(Render(todos));
            case "add":
            {
                var text = GetString(root, "text");
                if (string.IsNullOrWhiteSpace(text))
                    return ValueTask.FromResult("Error: text is required.");

                todos.Add(new SessionTodoItem
                {
                    Id = $"todo_{Guid.NewGuid():N}"[..17],
                    Text = text.Trim(),
                    Notes = GetString(root, "notes"),
                    Completed = false,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
                break;
            }
            case "update":
            case "complete":
            case "remove":
            {
                var id = GetString(root, "id");
                if (string.IsNullOrWhiteSpace(id))
                    return ValueTask.FromResult("Error: id is required.");

                var existing = todos.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (existing is null)
                    return ValueTask.FromResult($"Error: todo '{id}' was not found.");

                if (action == "remove")
                {
                    todos.Remove(existing);
                    break;
                }

                todos[todos.IndexOf(existing)] = new SessionTodoItem
                {
                    Id = existing.Id,
                    Text = GetString(root, "text") ?? existing.Text,
                    Notes = GetString(root, "notes") ?? existing.Notes,
                    Completed = action == "complete" || existing.Completed,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                break;
            }
            case "clear":
                todos.Clear();
                break;
            default:
                return ValueTask.FromResult("Error: Unknown action. Valid actions are list, add, update, complete, remove, and clear.");
        }

        _metadataStore.Set(context.Session.Id, new SessionMetadataUpdateRequest
        {
            ActivePresetId = metadata.ActivePresetId,
            Starred = metadata.Starred,
            Tags = metadata.Tags,
            TodoItems = todos
        });

        return ValueTask.FromResult(Render(todos));
    }

    private static string Render(IReadOnlyList<SessionTodoItem> todos)
    {
        if (todos.Count == 0)
            return "No todo items.";

        var sb = new StringBuilder();
        foreach (var todo in todos.OrderBy(static item => item.Completed).ThenBy(static item => item.CreatedAtUtc))
            sb.AppendLine($"{todo.Id} [{(todo.Completed ? "done" : "open")}] {todo.Text}");
        return sb.ToString().TrimEnd();
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
