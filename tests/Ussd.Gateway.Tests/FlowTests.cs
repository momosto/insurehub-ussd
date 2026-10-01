using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Ussd.Gateway.Core;
using Ussd.Gateway.Security;

namespace Ussd.Gateway.Tests;

/// <summary>Scripted dial sequences through the real HTTP endpoint (WebApplicationFactory), asserting each screen.</summary>
public sealed class FlowTests
{
    private const string Farai = "263733456789";
    private const string Tendai = "263772123456";
    private const string MaiChipo = "263779000111";
    private const string Nyasha = "263712987654";

    [Fact]
    public async Task Dial_shows_the_main_menu_without_calling_any_backend()
    {
        using var app = new TestApp();
        app.Fixtures.Chaos.Down = true;
        var screen = await app.Phone(Farai).Dial();
        Assert.StartsWith("CON Welcome to InsureHub", screen);
        Assert.Contains("2. Pay premium", screen);
    }

    [Fact]
    public async Task Callbacks_without_the_shared_secret_are_rejected()
    {
        using var app = new TestApp();
        var response = await app.CreateClient().PostAsync("/ussd", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["sessionId"] = "x", ["phoneNumber"] = Farai, ["text"] = "" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task First_use_sets_a_pin_by_otp_then_pays_the_arrears()
    {
        using var app = new TestApp();
        var phone = app.Phone(Farai);
        await phone.Dial();
        Assert.Contains("6-digit code", await phone.Reply("2"));
        Assert.Contains("Wrong code", await phone.Reply("000000"));
        Assert.Contains("Choose a 4-digit PIN", await phone.Reply(phone.LatestOtp()));
        Assert.Contains("easy to guess", await phone.Reply("1234"));
        Assert.Contains("again", await phone.Reply("4826"));
        var list = await phone.Reply("4826");
        Assert.Contains("PIN saved", list);
        Assert.Contains("1. MOT-..0301", list);
        Assert.DoesNotContain("MOT-2026-000301", list); // masked on screen (shared phones)
        Assert.Contains("Due: USD 187.20", await phone.Reply("1"));
        Assert.Contains("Pay USD 187.20 for MOT-..0301", await phone.Reply("1"));
        var done = await phone.Reply("1");
        Assert.StartsWith("END Approve the EcoCash prompt", done);
        var payment = Assert.Single(app.Fixtures.Payments);
        Assert.Equal(187.20m, payment.Amount);
        Assert.Equal("MOT-2026-000301", payment.Reference);
        Assert.StartsWith($"ussd:{phone.SessionId}:", payment.IdempotencyKey);
    }

    [Fact]
    public async Task Other_amount_is_validated_and_declining_charges_nothing()
    {
        using var app = new TestApp();
        var phone = app.Phone(Farai);
        await phone.SignIn("2");
        await phone.Reply("1");
        Assert.Contains("Enter the amount in USD", await phone.Reply("2"));
        Assert.Contains("valid amount", await phone.Reply("abc"));
        Assert.Contains("Pay USD 50.00", await phone.Reply("50"));
        Assert.Equal("END Cancelled. Nothing was charged.", await phone.Reply("2"));
        Assert.Empty(app.Fixtures.Payments);
    }

    [Fact]
    public async Task Returning_customer_enters_the_pin_and_sees_policy_details()
    {
        using var app = new TestApp();
        var phone = app.Phone(Tendai);
        await phone.SignIn("1");
        var list = await phone.SignIn("1"); // second session: PIN only
        Assert.Contains("Your policies:", list);
        Assert.Contains("2. FUN-..0102", list);
        var detail = await phone.Reply("1");
        Assert.StartsWith("END MOT-..0101", detail);
        Assert.Contains("Premium: USD 94.50/month", detail);
    }

    [Fact]
    public async Task Three_wrong_pins_lock_the_number_for_30_minutes()
    {
        using var app = new TestApp();
        var phone = app.Phone(Tendai);
        await phone.SignIn("1");
        await phone.Dial();
        await phone.Reply("1");
        Assert.Contains("2 tries left", await phone.Reply("1111"));
        Assert.Contains("1 tries left", await phone.Reply("2222"));
        Assert.StartsWith("END Too many wrong PINs", await phone.Reply("3333"));
        await phone.Dial();
        Assert.StartsWith("END Too many wrong PINs", await phone.Reply("1"));
    }

    [Fact]
    public async Task Claim_status_is_masked_and_ends_the_session()
    {
        using var app = new TestApp();
        var screen = await app.Phone(Tendai).SignIn("3");
        Assert.StartsWith("END PIN saved.\nCLM-..0111: UNDER REVIEW", screen);
        Assert.DoesNotContain("CLM-2026-000111", screen);
    }

    [Fact]
    public async Task Loan_instalment_is_paid_through_the_loan_flow()
    {
        using var app = new TestApp();
        var phone = app.Phone(MaiChipo);
        var view = await phone.SignIn("4");
        Assert.Contains("Loan LN-..0001", view);
        Assert.Contains("Balance: USD 554.88", view);
        Assert.Contains("Pay USD 57.03 on loan LN-..0001", await phone.Reply("1"));
        Assert.StartsWith("END Approve the EcoCash prompt", await phone.Reply("1"));
        var payment = Assert.Single(app.Fixtures.Payments);
        Assert.Equal("loan", payment.Kind);
        Assert.Equal("LN-2610-000001", payment.Reference);
    }

    [Fact]
    public async Task Customers_without_loans_or_policies_get_a_clear_end_message()
    {
        using var app = new TestApp();
        Assert.EndsWith("You have no loans with InsureHub Microfinance.", await app.Phone(Farai).SignIn("4"));
        Assert.EndsWith("You have no policies with us.", await app.Phone(MaiChipo).SignIn("1"));
    }

    [Fact]
    public async Task Recent_sim_swap_blocks_payments()
    {
        using var app = new TestApp();
        var phone = app.Phone(Nyasha);
        await phone.SignIn("2");
        await phone.Reply("1");
        await phone.Reply("1");
        Assert.StartsWith("END For your safety, payments are paused", await phone.Reply("1"));
        Assert.Empty(app.Fixtures.Payments);
    }

    [Fact]
    public async Task Unregistered_numbers_cannot_reach_personal_data()
    {
        using var app = new TestApp();
        var phone = app.Phone("263771999888");
        await phone.Dial();
        Assert.StartsWith("END This number is not registered", await phone.Reply("1"));
    }

    [Fact]
    public async Task Call_back_requests_reach_the_core_and_confirm_by_sms()
    {
        using var app = new TestApp();
        var phone = app.Phone(Farai);
        await phone.Dial();
        await phone.Reply("5");
        Assert.StartsWith("END Thank you. An agent will call you on ...6789", await phone.Reply("1"));
        Assert.Contains(app.Fixtures.Callbacks, c => c.Msisdn == Farai && c.Reason == "claim");
        Assert.Contains(app.Sms.For(Farai), m => m.Text.Contains("call you back about a claim"));
    }

    [Fact]
    public async Task Language_is_remembered_per_number()
    {
        using var app = new TestApp();
        var phone = app.Phone(MaiChipo);
        await phone.Dial();
        await phone.Reply("6");
        Assert.StartsWith("END Mutauro wachengetwa", await phone.Reply("2"));
        Assert.StartsWith("CON Mauya kuInsureHub", await phone.Dial());
        Assert.Contains("kodhi ine manhamba 6", await phone.Reply("4"));
        Assert.Contains("ndeiyi:", app.Sms.For(MaiChipo).Last().Text); // the OTP SMS is in Shona too
    }

    [Fact]
    public async Task Invalid_choices_repeat_the_screen()
    {
        using var app = new TestApp();
        var phone = app.Phone(Farai);
        await phone.Dial();
        var screen = await phone.Reply("9");
        Assert.StartsWith("CON Invalid choice.\nWelcome to InsureHub", screen);
    }

    [Fact]
    public async Task Long_lists_page_with_98_more_and_0_back()
    {
        var many = Enumerable.Range(1, 6).Select(i => new PolicySummary($"FUN-2026-00010{i}", "FUNERAL", $"Plan {i}", "ACTIVE", "USD", 10m, 0m)).ToList();
        using var app = new TestApp(s =>
        {
            s.AddSingleton<ICoreClient>(new FakeCore(many));
        });
        var phone = app.Phone(Tendai);
        var first = await phone.SignIn("1");
        Assert.Contains("3. FUN-..0103", first);
        Assert.Contains("98. More", first);
        var second = await phone.Reply("98");
        Assert.Contains("1. FUN-..0104", second);
        Assert.DoesNotContain("98. More", second);
        Assert.StartsWith("END FUN-..0106", await phone.Reply("3"));
    }

    [Fact]
    public async Task The_pin_and_input_history_never_reach_the_logs()
    {
        using var app = new TestApp();
        var phone = app.Phone(Farai);
        await phone.SignIn("2", pin: "4826");
        await phone.Reply("1");
        lock (app.Logs)
        {
            Assert.DoesNotContain(app.Logs, l => l.Contains("4826") || l.Contains("2*"));
        }
    }

    [Fact]
    public void Weak_pins_are_rejected_and_hashes_verify()
    {
        foreach (var weak in new[] { "0000", "1111", "1234", "4321", "6789", "1990", "2026", "12a4", "123" }) Assert.True(PinService.IsWeak(weak), weak);
        foreach (var ok in new[] { "4826", "7391", "0518" }) Assert.False(PinService.IsWeak(ok), ok);
        var hash = PinService.Hash("4826");
        Assert.StartsWith("argon2id$19456$2$1$", hash);
        Assert.True(PinService.Verify("4826", hash));
        Assert.False(PinService.Verify("4827", hash));
        Assert.NotEqual(hash, PinService.Hash("4826")); // salted
    }

    [Fact]
    public void References_and_numbers_are_masked()
    {
        Assert.Equal("MOT-..0301", Mask.Reference("MOT-2026-000301"));
        Assert.Equal("LN-..0001", Mask.Reference("LN-2610-000001"));
        Assert.Equal("...6789", Mask.Msisdn("263733456789"));
        Assert.Equal("263733456789", UssdService.NormaliseMsisdn("+263 73 345 6789"));
        Assert.Equal("263733456789", UssdService.NormaliseMsisdn("0733456789"));
    }

    private sealed class FakeCore(IReadOnlyList<PolicySummary> policies) : ICoreClient
    {
        public Task<CustomerRef?> FindCustomerAsync(string msisdn, CancellationToken ct) => Task.FromResult<CustomerRef?>(new("C1", "Test"));
        public Task<IReadOnlyList<PolicySummary>> PoliciesAsync(string customerRef, CancellationToken ct) => Task.FromResult(policies);
        public Task<IReadOnlyList<ClaimSummary>> ClaimsAsync(string customerRef, CancellationToken ct) => Task.FromResult<IReadOnlyList<ClaimSummary>>([]);
        public Task<IReadOnlyList<LoanSummary>> LoansAsync(string customerRef, CancellationToken ct) => Task.FromResult<IReadOnlyList<LoanSummary>>([]);
        public Task<PaymentStarted> StartPaymentAsync(PaymentRequest request, CancellationToken ct) => Task.FromResult(new PaymentStarted("p", "PENDING"));
        public Task RequestCallbackAsync(string msisdn, string? customerRef, string reason, CancellationToken ct) => Task.CompletedTask;
    }
}
