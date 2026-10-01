namespace Ussd.Gateway.Menus;

/// <summary>Checks a menu graph at startup and in tests: no unknown targets, no unreachable nodes, no dead ends,
/// every handler registered and every text key present in every language.</summary>
public static class MenuValidator
{
    public static IReadOnlyList<string> Validate(MenuDefinition menu, Texts texts, IReadOnlySet<string> handlers)
    {
        var errors = new List<string>();
        if (!menu.Nodes.ContainsKey(menu.Start)) errors.Add($"start node '{menu.Start}' missing");

        foreach (var (id, node) in menu.Nodes)
        {
            foreach (var target in Targets(node))
            {
                if (!menu.Nodes.ContainsKey(target)) errors.Add($"{id}: unknown target '{target}'");
            }
            if (node.Handler is not null && !handlers.Contains(node.Handler)) errors.Add($"{id}: handler '{node.Handler}' is not registered");
            var deadEnd = node.Type switch
            {
                NodeType.Menu => node.Options.Count == 0,
                NodeType.Pin or NodeType.List or NodeType.Input => node.Next is null,
                NodeType.View => !node.End && node.Options.Count == 0,
                NodeType.Action => node.Handler is null,
                _ => false,
            };
            if (deadEnd) errors.Add($"{id}: dead end ({node.Type} without a way forward)");
            if (node.Type == NodeType.Input && node.Var is null) errors.Add($"{id}: input without 'var'");

            foreach (var key in Keys(node))
            {
                foreach (var lang in Texts.Languages)
                {
                    if (!texts.Has(lang, key)) errors.Add($"{id}: text '{key}' missing in {lang}");
                }
            }
        }

        var reachable = new HashSet<string>();
        var queue = new Queue<string>([menu.Start]);
        while (queue.TryDequeue(out var id))
        {
            if (!reachable.Add(id) || !menu.Nodes.TryGetValue(id, out var node)) continue;
            foreach (var t in Targets(node)) queue.Enqueue(t);
        }
        errors.AddRange(menu.Nodes.Keys.Where(k => !reachable.Contains(k)).Select(k => $"{k}: unreachable from '{menu.Start}'"));
        return errors;
    }

    private static IEnumerable<string> Targets(MenuNode node) =>
        node.Options.Values.Select(o => o.To).Concat(node.Next is null ? [] : [node.Next]);

    private static IEnumerable<string> Keys(MenuNode node) =>
        node.Options.Values.Select(o => o.Label).Concat(node.Text is null ? [] : [node.Text]);
}
