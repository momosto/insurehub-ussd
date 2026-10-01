using Microsoft.Extensions.DependencyInjection;
using Ussd.Gateway.Menus;

namespace Ussd.Gateway.Tests;

/// <summary>The screen property (docs/05): every node, in every language, with worst-case realistic data, stays within
/// 182 GSM-7 characters. Also: the menu graph is valid and every text exists in every language.</summary>
public sealed class ScreenTests : IDisposable
{
    private readonly MenuRuntime _runtime;
    private readonly MenuDefinition _menu;
    private readonly Texts _texts;

    // longest values the data can produce: masked ref + 22-char description, 6-digit ZWG amounts, longest status
    private static readonly Dictionary<string, string> WorstCase = new()
    {
        ["item.label"] = Handlers.PoliciesHandler.Label("MOT-2026-000301", "Toyota Hilux 2.8 GD-6 AEZ 4521 - Comprehensive"),
        ["item.number"] = "MOT-2026-000301",
        ["item.status"] = "UNDER REVIEW",
        ["item.currency"] = "ZWG",
        ["item.premium"] = "999999.99",
        ["item.arrears"] = "999999.99",
        ["item.due"] = "999999.99",
        ["amount"] = "999999.99",
        ["currency"] = "ZWG",
        ["msisdn.masked"] = "...6789",
        ["loan.masked"] = "LN-..0001",
        ["loan.currency"] = "ZWG",
        ["loan.balance"] = "999999.99",
        ["loan.next"] = "999999.99",
        ["loan.due"] = "28 Feb",
        ["claims"] = "CLM-..0111: UNDER REVIEW. " + new string('x', 45) + "\nCLM-..0211: UNDER REVIEW. " + new string('x', 45),
        ["left"] = "2",
        ["code"] = "123456",
        ["reason"] = "something else",
        ["details"] = new string('x', 140),
        ["number"] = "CLM-..0111",
        ["status"] = "UNDER REVIEW",
        ["next"] = new string('x', 45),
    };

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    public ScreenTests()
    {
        var app = _app;
        _runtime = app.Services.GetRequiredService<MenuRuntime>();
        _menu = app.Services.GetRequiredService<MenuDefinition>();
        _texts = app.Services.GetRequiredService<Texts>();
    }

    public static TheoryData<string> Languages => new() { "en", "sn", "nd" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_menu_and_view_screen_fits_in_182_gsm7_characters(string language)
    {
        foreach (var (id, node) in _menu.Nodes.Where(n => n.Value.Type is NodeType.Menu or NodeType.View && n.Value.Text is not null))
        {
            foreach (var invalid in new[] { false, true })
            {
                var screen = _runtime.ComposeMenu(language, node, WorstCase, invalid);
                Assert.True(Texts.Gsm7Length(screen) <= Texts.MaxScreenLength, $"{language}/{id}: {Texts.Gsm7Length(screen)} chars\n{screen}");
                Assert.True(Texts.IsGsm7(screen), $"{language}/{id} has non-GSM-7 characters");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Full_list_pages_fit(string language)
    {
        var labels = Enumerable.Repeat(WorstCase["item.label"], MenuRuntime.PageSize).ToList();
        foreach (var (id, node) in _menu.Nodes.Where(n => n.Value.Type == NodeType.List))
        {
            var screen = _runtime.ComposeList(language, node, labels, hasMore: true, invalid: true);
            Assert.True(Texts.Gsm7Length(screen) <= Texts.MaxScreenLength, $"{language}/{id}: {Texts.Gsm7Length(screen)}\n{screen}");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_text_fits_and_is_gsm7_when_filled_with_worst_case_data(string language)
    {
        foreach (var (key, template) in _texts.Table(language))
        {
            var filled = Texts.Fill(template, WorstCase);
            Assert.True(Texts.IsGsm7(filled), $"{language}/{key} has non-GSM-7 characters");
            // SMS texts must fit one 160-character segment; screens leave room for a prefix such as "PIN saved."
            var limit = key.StartsWith("sms.") ? 160 : Texts.MaxScreenLength - 20;
            Assert.True(Texts.Gsm7Length(filled) <= limit, $"{language}/{key}: {Texts.Gsm7Length(filled)} chars");
        }
    }

    [Fact]
    public void Every_language_has_exactly_the_same_keys()
    {
        var en = _texts.Table("en").Keys.ToHashSet();
        foreach (var lang in new[] { "sn", "nd" })
        {
            Assert.Empty(en.Except(_texts.Table(lang).Keys));
            Assert.Empty(_texts.Table(lang).Keys.Except(en));
        }
    }

    [Fact]
    public void The_menu_graph_is_valid()
    {
        Assert.Empty(MenuValidator.Validate(_menu, _texts, _runtime.HandlerNames));
    }

    [Fact]
    public void The_validator_catches_broken_menus()
    {
        var broken = new MenuDefinition
        {
            Start = "main",
            Nodes = new()
            {
                ["main"] = new MenuNode { Type = NodeType.Menu, Text = "main.title", Options = new() { ["1"] = new MenuOption { Label = "no.such.text", To = "nowhere" } } },
                ["orphan"] = new MenuNode { Type = NodeType.List, Text = "pay.list", Handler = "Unknown" },
            },
        };
        var errors = MenuValidator.Validate(broken, _texts, _runtime.HandlerNames);
        Assert.Contains(errors, e => e.Contains("unknown target 'nowhere'"));
        Assert.Contains(errors, e => e.Contains("text 'no.such.text' missing"));
        Assert.Contains(errors, e => e.Contains("handler 'Unknown'"));
        Assert.Contains(errors, e => e.Contains("orphan: dead end"));
        Assert.Contains(errors, e => e.Contains("orphan: unreachable"));
    }

    [Fact]
    public void Non_gsm_characters_from_data_are_replaced()
    {
        Assert.Equal("Mai Chipo's \"plan\" - 2026", Texts.ToGsm7("Mai Chipo’s “plan” – 2026"));
        Assert.Equal(2, Texts.Gsm7Length("€"));
    }
}
