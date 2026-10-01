using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ussd.Gateway.Core;
using Ussd.Gateway.Handlers;
using Ussd.Gateway.Messaging;
using Ussd.Gateway.Observability;
using Ussd.Gateway.Security;
using Ussd.Gateway.Sessions;

namespace Ussd.Gateway.Menus;

/// <summary>Walks the menu graph: renders the current node, applies the latest input, moves to the next node.
/// All screens are localised, GSM-7 cleaned and checked against the 182-character limit.</summary>
public sealed class MenuRuntime
{
    public const int PageSize = 3;
    private const string MoreKey = "98";

    private readonly MenuDefinition _menu;
    private readonly Texts _texts;
    private readonly ICoreClient _core;
    private readonly PinService _pins;
    private readonly ISmsSender _sms;
    private readonly UssdMetrics _metrics;
    private readonly ILogger<MenuRuntime> _log;
    private readonly Dictionary<string, IMenuHandler> _handlers;

    public MenuRuntime(MenuDefinition menu, Texts texts, ICoreClient core, PinService pins, ISmsSender sms,
        IEnumerable<IMenuHandler> handlers, UssdMetrics metrics, ILogger<MenuRuntime> log)
    {
        _menu = menu;
        _texts = texts;
        _core = core;
        _pins = pins;
        _sms = sms;
        _metrics = metrics;
        _log = log;
        _handlers = handlers.ToDictionary(h => h.Name);
    }

    public IReadOnlySet<string> HandlerNames => _handlers.Keys.ToHashSet();

    public Task<Screen> StartAsync(UssdSession session, CancellationToken ct) => EnterAsync(session, _menu.Start, ct);

    public async Task<Screen> HandleAsync(UssdSession session, string input, CancellationToken ct)
    {
        session.Steps++;
        var node = _menu[session.Node];
        input = input.Trim();
        switch (node.Type)
        {
            case NodeType.Menu:
            {
                if (!node.Options.TryGetValue(input, out var option)) return await RenderAsync(session, node, ct, invalid: true);
                foreach (var (k, v) in option.Set) session.Vars[k] = Texts.Fill(v, session.Vars);
                return await EnterAsync(session, option.To, ct);
            }
            case NodeType.List:
            {
                if (input == MoreKey && (session.Page + 1) * PageSize < session.ListItems.Count)
                {
                    session.Page++;
                    return RenderList(session, node);
                }
                if (input == "0") return await EnterAsync(session, _menu.Start, ct);
                var index = int.TryParse(input, out var n) ? session.Page * PageSize + n - 1 : -1;
                if (n < 1 || n > PageSize || index >= session.ListItems.Count) return RenderList(session, node, invalid: true);
                foreach (var (k, v) in session.ListItems[index]) session.Vars[$"item.{k}"] = v;
                session.ListItems.Clear();
                return await EnterAsync(session, node.Next!, ct);
            }
            case NodeType.Input:
            {
                if (node.Validate == "amount" && !TryAmount(input, out var amount))
                    return Screen.Con(Clean(T(session, "pay.invalid") + "\n" + T(session, node.Text!)));
                session.Vars[node.Var!] = node.Validate == "amount" ? Fmt.Money(ParseAmount(input)) : input;
                return await EnterAsync(session, node.Next!, ct);
            }
            case NodeType.Pin:
                return await PinStepAsync(session, node, input, ct);
            case NodeType.View when !node.End:
                return node.Options.TryGetValue(input, out var viewOption)
                    ? await EnterAsync(session, viewOption.To, ct)
                    : await RenderAsync(session, node, ct, invalid: true);
            default:
                return Screen.End(T(session, "session.error"));
        }
    }

    // ------------------------------------------------------------------------------------------------ navigation

    private async Task<Screen> EnterAsync(UssdSession session, string nodeId, CancellationToken ct)
    {
        session.Node = nodeId;
        var node = _menu[nodeId];
        _metrics.Viewed(nodeId);
        switch (node.Type)
        {
            case NodeType.Menu or NodeType.View:
                return await RenderAsync(session, node, ct);
            case NodeType.Input:
                return Screen.Con(Clean(T(session, node.Text!)));
            case NodeType.Pin:
                return await StartPinAsync(session, node, ct);
            case NodeType.List:
            {
                var handler = (IListHandler)_handlers[node.Handler!];
                session.ListItems = (await handler.ItemsAsync(Context(session, ct))).ToList();
                session.Page = 0;
                if (session.ListItems.Count == 0) return End(session, nodeId, T(session, "list.empty"));
                return RenderList(session, node);
            }
            case NodeType.Action:
            {
                var screen = await ((IActionHandler)_handlers[node.Handler!]).ExecuteAsync(Context(session, ct));
                return screen.Continue ? screen : End(session, nodeId, screen.Text, completed: true);
            }
            case NodeType.End:
                return End(session, nodeId, T(session, node.Text!), completed: nodeId != "cancelled");
            default:
                throw new InvalidOperationException($"Cannot enter {node.Type}");
        }
    }

    private async Task<Screen> RenderAsync(UssdSession session, MenuNode node, CancellationToken ct, bool invalid = false)
    {
        if (node.Handler is not null && !invalid)
        {
            var early = await ((IViewHandler)_handlers[node.Handler]).PrepareAsync(Context(session, ct));
            if (early is not null) return End(session, session.Node, early.Text);
        }
        var text = Clean(ComposeMenu(session.Language, node, Vars(session), invalid));
        return node.Type == NodeType.View && node.End ? End(session, session.Node, text, completed: true) : Screen.Con(text);
    }

    private Screen RenderList(UssdSession session, MenuNode node, bool invalid = false)
    {
        var page = session.ListItems.Skip(session.Page * PageSize).Take(PageSize).Select(i => i["label"]).ToList();
        var hasMore = (session.Page + 1) * PageSize < session.ListItems.Count;
        return Screen.Con(Clean(ComposeList(session.Language, node, page, hasMore, invalid)));
    }

    /// <summary>Menu/view screen: body + numbered options. Pure, so tests can check every node at worst-case lengths.</summary>
    internal string ComposeMenu(string language, MenuNode node, IReadOnlyDictionary<string, string> vars, bool invalid = false)
    {
        var sb = new StringBuilder();
        if (invalid) sb.Append(_texts.Get(language, "common.invalid")).Append('\n');
        sb.Append(_texts.Get(language, node.Text!, vars));
        foreach (var (key, option) in node.Options)
        {
            sb.Append('\n').Append(key).Append(". ").Append(_texts.Get(language, option.Label, vars));
        }
        return sb.ToString();
    }

    internal string ComposeList(string language, MenuNode node, IReadOnlyList<string> labels, bool hasMore, bool invalid = false)
    {
        var sb = new StringBuilder();
        if (invalid) sb.Append(_texts.Get(language, "common.invalid")).Append('\n');
        sb.Append(_texts.Get(language, node.Text!));
        for (var i = 0; i < labels.Count; i++) sb.Append('\n').Append(i + 1).Append(". ").Append(labels[i]);
        if (hasMore) sb.Append('\n').Append(MoreKey).Append(". ").Append(_texts.Get(language, "common.more"));
        sb.Append("\n0. ").Append(_texts.Get(language, "common.back"));
        return sb.ToString();
    }

    private Screen End(UssdSession session, string nodeId, string text, bool completed = false)
    {
        _metrics.Ended(nodeId, completed);
        return Screen.End(Clean(text));
    }

    // ------------------------------------------------------------------------------------------------ PIN (US-02)

    private async Task<Screen> StartPinAsync(UssdSession session, MenuNode node, CancellationToken ct)
    {
        if (session.CustomerRef is null)
        {
            var customer = await _core.FindCustomerAsync(session.Msisdn, ct);
            if (customer is null) return End(session, session.Node, T(session, "not.registered"));
            session.CustomerRef = customer.Value;
        }
        if (session.PinVerified) return await EnterAsync(session, node.Next!, ct);
        if (await _pins.IsLockedAsync(session.Msisdn)) return End(session, session.Node, T(session, "pin.locked"));
        if (await _pins.HasPinAsync(session.Msisdn))
        {
            session.Vars["pin.stage"] = "verify";
            return Screen.Con(Clean(T(session, "pin.enter")));
        }
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        session.Vars["pin.stage"] = "otp";
        session.Vars["pin.otp"] = Sha(otp);
        session.Vars["pin.otpTries"] = "0";
        _sms.Send(session.Msisdn, T(session, "sms.otp", new Dictionary<string, string> { ["code"] = otp }));
        return Screen.Con(Clean(T(session, "pin.otp")));
    }

    private async Task<Screen> PinStepAsync(UssdSession session, MenuNode node, string input, CancellationToken ct)
    {
        var v = session.Vars;
        switch (v.GetValueOrDefault("pin.stage"))
        {
            case "verify":
            {
                var (result, left) = await _pins.VerifyAsync(session.Msisdn, input);
                if (result == PinCheck.Ok)
                {
                    session.PinVerified = true;
                    v.Remove("pin.stage");
                    return await EnterAsync(session, node.Next!, ct);
                }
                return result == PinCheck.Locked
                    ? End(session, session.Node, T(session, "pin.locked"))
                    : Screen.Con(Clean(T(session, "pin.wrong", new Dictionary<string, string> { ["left"] = left.ToString(CultureInfo.InvariantCulture) })));
            }
            case "otp":
            {
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sha(input)), Encoding.ASCII.GetBytes(v["pin.otp"])))
                {
                    var tries = int.Parse(v["pin.otpTries"], CultureInfo.InvariantCulture) + 1;
                    v["pin.otpTries"] = tries.ToString(CultureInfo.InvariantCulture);
                    return tries >= PinService.MaxAttempts ? End(session, session.Node, T(session, "pin.locked"))
                        : Screen.Con(Clean(T(session, "pin.otp.wrong")));
                }
                v.Remove("pin.otp");
                v["pin.stage"] = "new";
                return Screen.Con(Clean(T(session, "pin.new")));
            }
            case "new":
                if (PinService.IsWeak(input)) return Screen.Con(Clean(T(session, "pin.weak")));
                v["pin.new"] = Sha(session.SessionId + input); // never keep the PIN itself, even in the 180 s session
                v["pin.stage"] = "repeat";
                return Screen.Con(Clean(T(session, "pin.repeat")));
            case "repeat":
                if (Sha(session.SessionId + input) != v["pin.new"])
                {
                    v["pin.stage"] = "new";
                    return Screen.Con(Clean(T(session, "pin.mismatch")));
                }
                await _pins.SetPinAsync(session.Msisdn, input);
                foreach (var k in new[] { "pin.stage", "pin.new", "pin.otpTries" }) v.Remove(k);
                session.PinVerified = true;
                var next = await EnterAsync(session, node.Next!, ct);
                return next with { Text = Clean(T(session, "pin.saved") + "\n" + next.Text) };
            default:
                return await StartPinAsync(session, node, ct);
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private MenuContext Context(UssdSession session, CancellationToken ct) => new(session, _core, _texts, ct);

    private string T(UssdSession session, string key, IReadOnlyDictionary<string, string>? vars = null) =>
        _texts.Get(session.Language, key, vars ?? Vars(session));

    private static Dictionary<string, string> Vars(UssdSession session) =>
        new(session.Vars) { ["msisdn.masked"] = Mask.Msisdn(session.Msisdn) };

    /// <summary>GSM-7 only and never longer than 182 characters; a too-long screen is a bug, logged and trimmed.</summary>
    private string Clean(string text)
    {
        var gsm = Texts.ToGsm7(text);
        if (Texts.Gsm7Length(gsm) <= Texts.MaxScreenLength) return gsm;
        _log.LogError("Screen over {Max} characters at a node; trimming", Texts.MaxScreenLength);
        return gsm[..(Texts.MaxScreenLength - 2)] + "..";
    }

    private static bool TryAmount(string input, out decimal amount) =>
        decimal.TryParse(input.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) && amount > 0 && amount < 1_000_000;

    private static decimal ParseAmount(string input) =>
        Math.Round(decimal.Parse(input.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), 2);

    private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
