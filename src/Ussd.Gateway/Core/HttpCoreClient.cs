using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ussd.Gateway.Core;

public sealed class CoreOptions
{
    public string InsureHubUrl { get; set; } = "http://localhost:5080";
    public string LendHubUrl { get; set; } = "http://localhost:8080";
    public string PaymentsUrl { get; set; } = "http://localhost:5100";
    public string InsureAssistUrl { get; set; } = "http://localhost:8100";
    public string LendHubUser { get; set; } = "channel@lendhub.demo";
    public string LendHubPassword { get; set; } = "Demo123!";
    public string EventsSecret { get; set; } = "dev-insurehub-callback-secret";
}

/// <summary>Real core APIs. LendHub's channel endpoints exist; InsureHub's (/api/channels/*) follow the contract
/// in insurehub/NEXT_STEPS.md; call-backs become a handoff in the InsureAssist inbox via its signed /events endpoint.
/// Resilience (1.5 s timeout, circuit breaker) is applied by <see cref="ResilientCoreClient"/>.</summary>
public sealed class HttpCoreClient(IHttpClientFactory http, CoreOptions options, TimeProvider clock) : ICoreClient
{
    private string? _lendHubToken;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string name) => http.CreateClient(name);

    private async Task<HttpResponseMessage> LendHubAsync(HttpMethod method, string path, object? body, string? idempotencyKey,
        CancellationToken ct)
    {
        var client = Client("lendhub");
        if (_lendHubToken is null)
        {
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { username = options.LendHubUser, password = options.LendHubPassword }, ct);
            login.EnsureSuccessStatusCode();
            _lendHubToken = (await login.Content.ReadFromJsonAsync<JsonObject>(ct))!["accessToken"]!.GetValue<string>();
        }
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _lendHubToken);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) _lendHubToken = null;
        return response;
    }

    public async Task<CustomerRef?> FindCustomerAsync(string msisdn, CancellationToken ct)
    {
        var ih = await Client("insurehub").GetAsync($"/api/channels/customers?msisdn={msisdn}", ct);
        if (ih.IsSuccessStatusCode)
        {
            var list = await ih.Content.ReadFromJsonAsync<JsonArray>(ct);
            if (list is { Count: > 0 }) return new(list[0]!["customerRef"]!.GetValue<string>(), list[0]!["firstName"]!.GetValue<string>());
        }
        var lh = await LendHubAsync(HttpMethod.Get, $"/api/v1/customers?msisdn={msisdn}", null, null, ct);
        if (!lh.IsSuccessStatusCode) return null;
        var found = await lh.Content.ReadFromJsonAsync<JsonArray>(ct);
        return found is { Count: > 0 } ? new(found[0]!["customerRef"]!.GetValue<string>(), found[0]!["firstName"]!.GetValue<string>()) : null;
    }

    public async Task<IReadOnlyList<PolicySummary>> PoliciesAsync(string customerRef, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/channels/policies");
        req.Headers.Add("X-On-Behalf-Of", customerRef);
        var r = await Client("insurehub").SendAsync(req, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        r.EnsureSuccessStatusCode();
        var rows = await r.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        return rows.Select(p => new PolicySummary(p!["policyNumber"]!.GetValue<string>(), p["line"]!.GetValue<string>(),
            p["description"]!.GetValue<string>(), p["status"]!.GetValue<string>(), p["currency"]!.GetValue<string>(),
            p["monthlyPremium"]!.GetValue<decimal>(), p["arrears"]?.GetValue<decimal>() ?? 0m)).ToList();
    }

    public async Task<IReadOnlyList<ClaimSummary>> ClaimsAsync(string customerRef, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/channels/claims");
        req.Headers.Add("X-On-Behalf-Of", customerRef);
        var r = await Client("insurehub").SendAsync(req, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        r.EnsureSuccessStatusCode();
        var rows = await r.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        return rows.Select(c => new ClaimSummary(c!["claimNumber"]!.GetValue<string>(), c["status"]!.GetValue<string>(),
            c["nextStep"]?.GetValue<string>() ?? "")).ToList();
    }

    public async Task<IReadOnlyList<LoanSummary>> LoansAsync(string customerRef, CancellationToken ct)
    {
        var r = await LendHubAsync(HttpMethod.Get, $"/api/v1/customers/{customerRef}/loans", null, null, ct);
        r.EnsureSuccessStatusCode();
        var rows = await r.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        return rows.Select(l => new LoanSummary(l!["loanId"]!.GetValue<string>(), l["loanNumber"]!.GetValue<string>(),
            l["currency"]!.GetValue<string>(), l["outstandingPrincipal"]!.GetValue<decimal>(), l["nextAmountDue"]!.GetValue<decimal>(),
            l["nextDueDate"] is { } d ? DateOnly.Parse(d.GetValue<string>(), CultureInfo.InvariantCulture) : null,
            l["arrearsAmount"]!.GetValue<decimal>())).ToList();
    }

    public async Task<PaymentStarted> StartPaymentAsync(PaymentRequest p, CancellationToken ct)
    {
        if (p.Kind == "loan")
        {
            var loan = (await LoansAsync(p.CustomerRef, ct)).First(l => l.LoanNumber == p.Reference);
            var r = await LendHubAsync(HttpMethod.Post, $"/api/v1/loans/{loan.LoanId}/repayment-requests",
                new { amount = p.Amount, msisdn = p.Msisdn }, p.IdempotencyKey, ct);
            r.EnsureSuccessStatusCode();
            var body = await r.Content.ReadFromJsonAsync<JsonObject>(ct);
            return new(body?["paymentId"]?.ToString() ?? "", body?["status"]?.ToString() ?? "PENDING");
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new { policyNumber = p.Reference, amount = p.Amount, currency = p.Currency, method = "EcoCash", msisdn = p.Msisdn }),
        };
        req.Headers.Add("Idempotency-Key", p.IdempotencyKey);
        var pay = await Client("payments").SendAsync(req, ct);
        pay.EnsureSuccessStatusCode();
        var started = await pay.Content.ReadFromJsonAsync<JsonObject>(ct);
        return new(started!["id"]!.ToString(), started["status"]!.ToString());
    }

    public async Task RequestCallbackAsync(string msisdn, string? customerRef, string reason, CancellationToken ct)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            messageId = Guid.NewGuid(),
            type = "CallbackRequested",
            occurredAt = clock.GetUtcNow(),
            payload = JsonSerializer.Serialize(new { msisdn, customerRef, reason, channel = "ussd" }, Web),
        }, Web);
        var t = clock.GetUtcNow().ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.EventsSecret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{envelope}"))).ToLowerInvariant();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/events") { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Signature", $"t={t},v1={sig}");
        var r = await Client("insureassist").SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
    }
}
