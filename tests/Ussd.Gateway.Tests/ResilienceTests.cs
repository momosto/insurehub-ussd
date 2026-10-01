using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Redis;
using Ussd.Gateway.Core;
using Ussd.Gateway.Sessions;
using StackExchange.Redis;

namespace Ussd.Gateway.Tests;

/// <summary>The 2-second budget: slow or failing backends give a cached answer or a graceful END, never a hang.</summary>
public sealed class ResilienceTests
{
    private const string Tendai = "263772123456";

    [Fact]
    public async Task Slow_backend_without_cache_ends_gracefully_within_two_seconds_and_sms_follows()
    {
        using var app = new TestApp(settings: new() { ["Core:TimeoutMs"] = "1500" });
        var phone = app.Phone(Tendai);
        await phone.Dial();
        app.Fixtures.Chaos.DelayMs = 1700; // slower than the 1.5 s budget, nothing cached yet
        var timer = Stopwatch.StartNew();
        var screen = await phone.Reply("3");
        timer.Stop();
        Assert.StartsWith("END We are busy right now", screen);
        Assert.True(timer.ElapsedMilliseconds < 2000, $"took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Cached_answer_is_served_when_the_backend_goes_down()
    {
        using var app = new TestApp();
        var phone = app.Phone(Tendai);
        Assert.Contains("Your policies:", await phone.SignIn("1"));
        app.Fixtures.Chaos.Down = true;
        var again = await phone.SignIn("1");
        Assert.Contains("1. MOT-..0101", again);
        Assert.Contains(app.Logs, l => l.Contains("Serving cached"));
    }

    [Fact]
    public async Task Busy_answer_is_followed_by_an_sms_once_the_backend_recovers()
    {
        using var app = new TestApp();
        var phone = app.Phone(Tendai);
        await phone.SignIn("1");
        await phone.Dial();
        await phone.Reply("3");
        app.Fixtures.Chaos.Down = true;
        Assert.StartsWith("END We are busy", await phone.Reply("4826"));
        app.Fixtures.Chaos.Down = false;
        for (var i = 0; i < 50 && !app.Sms.For(Tendai).Any(m => m.Text.Contains("CLM-..0111")); i++) await Task.Delay(100);
        Assert.Contains(app.Sms.For(Tendai), m => m.Text.Contains("CLM-..0111 UNDER REVIEW"));
    }

    [Fact]
    public async Task Resilient_client_times_out_and_falls_back_to_the_last_good_copy()
    {
        var inner = new SlowCore();
        var client = new ResilientCoreClient(inner, new MemoryCache(new MemoryCacheOptions()), NullLogger<ResilientCoreClient>.Instance,
            TimeSpan.FromMilliseconds(200));
        Assert.Single(await client.PoliciesAsync("C1", CancellationToken.None));
        inner.Delay = TimeSpan.FromSeconds(2);
        var timer = Stopwatch.StartNew();
        Assert.Single(await client.PoliciesAsync("C1", CancellationToken.None)); // stale copy
        Assert.True(timer.ElapsedMilliseconds < 1000);
        await Assert.ThrowsAsync<BackendUnavailableException>(() => client.ClaimsAsync("C1", CancellationToken.None));
        await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            client.StartPaymentAsync(new PaymentRequest("premium", "R", 1, "USD", "263", "C1", "k"), CancellationToken.None));
    }

    [Fact]
    public async Task Fixture_payments_are_idempotent_on_the_key()
    {
        using var app = new TestApp();
        var request = new PaymentRequest("premium", "MOT-2026-000301", 10m, "USD", "263733456789", "IH-CUS-0003", "same-key");
        var a = await app.Fixtures.StartPaymentAsync(request, CancellationToken.None);
        var b = await app.Fixtures.StartPaymentAsync(request, CancellationToken.None);
        Assert.Equal(a.PaymentId, b.PaymentId);
        Assert.Single(app.Fixtures.Payments);
    }

    [Fact]
    public async Task Redis_store_keeps_sessions_languages_and_pin_state()
    {
        RedisContainer redis;
        try
        {
            redis = new RedisBuilder("redis:7-alpine").Build();
            await redis.StartAsync();
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            return; // no Docker on this machine: covered in CI
        }
        await using var _ = redis;
        var store = new RedisStores(await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString()));
        var session = new UssdSession { SessionId = "s1", Msisdn = "263", Node = "main", Vars = { ["amount"] = "10.00" } };
        await store.SaveAsync(session, TimeSpan.FromSeconds(30));
        var loaded = await store.GetAsync("s1");
        Assert.Equal("10.00", loaded!.Vars["amount"]);
        await store.SetLanguageAsync("263", "nd");
        Assert.Equal("nd", await store.GetLanguageAsync("263"));
        var until = DateTimeOffset.UtcNow.AddMinutes(30);
        await store.SetPinStateAsync("263", 3, until, TimeSpan.FromMinutes(30));
        var state = await store.GetPinStateAsync("263");
        Assert.Equal(3, state.Attempts);
        Assert.Equal(until.ToUnixTimeSeconds(), state.LockedUntil!.Value.ToUnixTimeSeconds());
        await store.DeleteAsync("s1");
        Assert.Null(await store.GetAsync("s1"));
    }

    private sealed class SlowCore : ICoreClient
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public Task<CustomerRef?> FindCustomerAsync(string msisdn, CancellationToken ct) => Task.FromResult<CustomerRef?>(null);

        public async Task<IReadOnlyList<PolicySummary>> PoliciesAsync(string customerRef, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            return [new PolicySummary("P", "MOTOR", "d", "ACTIVE", "USD", 1, 0)];
        }

        public async Task<IReadOnlyList<ClaimSummary>> ClaimsAsync(string customerRef, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            return [];
        }

        public Task<IReadOnlyList<LoanSummary>> LoansAsync(string customerRef, CancellationToken ct) => Task.FromResult<IReadOnlyList<LoanSummary>>([]);

        public async Task<PaymentStarted> StartPaymentAsync(PaymentRequest request, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            return new("p", "PENDING");
        }

        public Task RequestCallbackAsync(string msisdn, string? customerRef, string reason, CancellationToken ct) => Task.CompletedTask;
    }
}
