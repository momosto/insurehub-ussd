using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ussd.Gateway.Menus;

/// <summary>A menu graph loaded from <c>menus/*.json</c> (ADR-0001: menus are data, handlers are small classes).</summary>
public sealed class MenuDefinition
{
    public required string Start { get; init; }
    public required Dictionary<string, MenuNode> Nodes { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static MenuDefinition Load(string path) =>
        JsonSerializer.Deserialize<MenuDefinition>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException($"Empty menu file {path}");

    public MenuNode this[string id] =>
        Nodes.TryGetValue(id, out var node) ? node : throw new KeyNotFoundException($"Unknown menu node '{id}'");
}

public enum NodeType { Menu, Pin, List, Input, View, Action, End }

public sealed class MenuNode
{
    public NodeType Type { get; init; }
    /// <summary>Text key in i18n/*.json; may contain {placeholders}.</summary>
    public string? Text { get; init; }
    public Dictionary<string, MenuOption> Options { get; init; } = new();
    public string? Next { get; init; }
    public string? Handler { get; init; }
    /// <summary>Input nodes: the session variable that receives the value.</summary>
    public string? Var { get; init; }
    /// <summary>Input nodes: validation rule ("amount").</summary>
    public string? Validate { get; init; }
    /// <summary>View nodes: true = the screen ends the session.</summary>
    public bool End { get; init; }
}

public sealed class MenuOption
{
    public required string Label { get; init; }
    public required string To { get; init; }
    /// <summary>Session variables to set when the option is chosen; values may reference other variables, e.g. "{item.due}".</summary>
    public Dictionary<string, string> Set { get; init; } = new();
}
