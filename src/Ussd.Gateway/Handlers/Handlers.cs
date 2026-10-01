using System.Globalization;
using Ussd.Gateway.Core;
using Ussd.Gateway.Menus;
using Ussd.Gateway.Messaging;
using Ussd.Gateway.Security;
using Ussd.Gateway.Sessions;

namespace Ussd.Gateway.Handlers;

/// <summary>What a handler sees: the session, the core systems and the text tables.</summary>
public sealed record MenuContext(UssdSession Session, ICoreClient Core, Texts Texts, CancellationToken Ct)
{
    public string Lang => Session.Language;
    public string CustomerRef => Session.CustomerRef ?? throw new InvalidOperationException("customer not resolved");
    public string T(string key, IReadOnlyDictionary<string, string>? vars = null) => Texts.Get(Lang, key, vars);
}

public sealed record Screen(bool Continue, string Text)
{
    public static Screen Con(string text) => new(true, text);
    public static Screen End(string text) => new(false, text);
}

public interface IMenuHandler
{
    string Name { get; }
}

/// <summary>Supplies the rows of a list node; each row's variables become <c>item.*</c> when selected.</summary>
public interface IListHandler : IMenuHandler
{
    Task<IReadOnlyList<Dictionary<string, string>>> ItemsAsync(MenuContext ctx);
}

/// <summary>Prepares variables for a view or menu node, or ends the session with its own message.</summary>
public interface IViewHandler : IMenuHandler
{
    Task<Screen?> PrepareAsync(MenuContext ctx);
}

public interface IActionHandler : IMenuHandler
{
    Task<Screen> ExecuteAsync(MenuContext ctx);
}

internal static class Fmt
{
    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed class PoliciesHandler : IListHandler
{
    public string Name => "Policies";

    public async Task<IReadOnlyList<Dictionary<string, string>>> ItemsAsync(MenuContext ctx) =>
        (await ctx.Core.PoliciesAsync(ctx.CustomerRef, ctx.Ct)).Select(p => new Dictionary<string, string>
        {
            ["label"] = Label(p.Number, p.Description),
            ["number"] = p.Number,
            ["status"] = p.Status,
            ["currency"] = p.Currency,
            ["premium"] = Fmt.Money(p.MonthlyPremium),
            ["arrears"] = Fmt.Money(p.Arrears),
            ["due"] = Fmt.Money(p.Arrears > 0 ? p.Arrears : p.MonthlyPremium),
        }).ToList();

    /// <summary>Masked reference + short description; sized so a full page fits 182 characters in every language.</summary>
    internal static string Label(string number, string description) => $"{Mask.Reference(number)} {Shorten(description, 16)}";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 2)] + "..";
}

/// <summary>Detail and amount screens read the selected item's variables; nothing to fetch.</summary>
public sealed class SelectedItemHandler(string name) : IViewHandler
{
    public string Name => name;
    public Task<Screen?> PrepareAsync(MenuContext ctx) => Task.FromResult<Screen?>(null);
}

public sealed class ClaimsHandler : IViewHandler
{
    public string Name => "Claims";

    internal static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 2)] + "..";

    public async Task<Screen?> PrepareAsync(MenuContext ctx)
    {
        var claims = await ctx.Core.ClaimsAsync(ctx.CustomerRef, ctx.Ct);
        if (claims.Count == 0) return Screen.End(ctx.T("claims.none"));
        // the latest two claims fit in one screen; amounts in dispute are never shown (US-05)
        ctx.Session.Vars["claims"] = string.Join("\n", claims.Take(2).Select(c => ctx.T("claims.line",
            new Dictionary<string, string> { ["number"] = Mask.Reference(c.Number), ["status"] = c.Status, ["next"] = Shorten(c.NextStep, 45) })));
        return null;
    }
}

public sealed class LoanSummaryHandler : IViewHandler
{
    public string Name => "LoanSummary";

    public async Task<Screen?> PrepareAsync(MenuContext ctx)
    {
        var loan = (await ctx.Core.LoansAsync(ctx.CustomerRef, ctx.Ct)).FirstOrDefault();
        if (loan is null) return Screen.End(ctx.T("loan.none"));
        var v = ctx.Session.Vars;
        v["loan.number"] = loan.LoanNumber;
        v["loan.masked"] = Mask.Reference(loan.LoanNumber);
        v["loan.currency"] = loan.Currency;
        v["loan.balance"] = Fmt.Money(loan.Outstanding);
        v["loan.next"] = Fmt.Money(loan.Arrears > 0 ? loan.Arrears + loan.NextAmount : loan.NextAmount);
        v["loan.due"] = loan.NextDueDate?.ToString("dd MMM", CultureInfo.InvariantCulture) ?? "-";
        return null;
    }
}

/// <summary>US-04/US-06: confirmation already shown; SIM-swap check, limits, then Payments with an idempotency key
/// derived from the session so a resent request cannot charge twice.</summary>
public sealed class StartPaymentHandler(string name, string kind, ISimSwapChecker simSwap, IConfiguration config) : IActionHandler
{
    public string Name => name;

    public async Task<Screen> ExecuteAsync(MenuContext ctx)
    {
        var v = ctx.Session.Vars;
        var reference = kind == "loan" ? v["loan.number"] : v["item.number"];
        var currency = kind == "loan" ? v["loan.currency"] : v["item.currency"];
        var amount = decimal.Parse(v["amount"], CultureInfo.InvariantCulture);
        if (await simSwap.SwappedRecentlyAsync(ctx.Session.Msisdn)) return Screen.End(ctx.T("pay.simswap"));
        var limit = config.GetValue<decimal>($"Ussd:PaymentLimit:{currency}", currency == "ZWG" ? 30000m : 1000m);
        if (amount > limit) return Screen.End(ctx.T("pay.limit"));
        var key = $"ussd:{ctx.Session.SessionId}:{reference}:{Fmt.Money(amount)}";
        await ctx.Core.StartPaymentAsync(new PaymentRequest(kind, reference, amount, currency, ctx.Session.Msisdn,
            ctx.CustomerRef, key), ctx.Ct);
        return Screen.End(ctx.T("pay.started", new Dictionary<string, string> { ["currency"] = currency, ["amount"] = Fmt.Money(amount) }));
    }
}

public sealed class RequestCallbackHandler(ISmsSender sms) : IActionHandler
{
    public string Name => "RequestCallback";

    public async Task<Screen> ExecuteAsync(MenuContext ctx)
    {
        var reason = ctx.Session.Vars.GetValueOrDefault("reason", "other");
        await ctx.Core.RequestCallbackAsync(ctx.Session.Msisdn, ctx.Session.CustomerRef, reason, ctx.Ct);
        var label = ctx.T($"callback.{reason}");
        sms.Send(ctx.Session.Msisdn, ctx.T("sms.callback", new Dictionary<string, string> { ["reason"] = label.ToLowerInvariant() }));
        return Screen.End(ctx.T("callback.done", new Dictionary<string, string> { ["msisdn.masked"] = Mask.Msisdn(ctx.Session.Msisdn) }));
    }
}

public sealed class SetLanguageHandler(ISessionStore store) : IActionHandler
{
    public string Name => "SetLanguage";

    public async Task<Screen> ExecuteAsync(MenuContext ctx)
    {
        var lang = ctx.Session.Vars.GetValueOrDefault("language", "en");
        await store.SetLanguageAsync(ctx.Session.Msisdn, lang);
        ctx.Session.Language = lang;
        return Screen.End(ctx.T("language.saved"));
    }
}
