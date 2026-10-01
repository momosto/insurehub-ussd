using System.Collections.Concurrent;
using Ussd.Gateway.Messaging;

namespace Ussd.Gateway.Core;

/// <summary>Demo core systems mirroring the InsureHub and LendHub seeds (same customers as InsureAssist).
/// <see cref="Chaos"/> lets the demo slow down or "kill" the backend to show the 2-second budget.</summary>
public sealed class FixtureCoreClient(ISmsSender sms, TimeProvider clock) : ICoreClient
{
    public sealed class ChaosSettings
    {
        public int DelayMs { get; set; }
        public bool Down { get; set; }
    }

    public ChaosSettings Chaos { get; } = new();
    public ConcurrentQueue<PaymentRequest> Payments { get; } = new();
    public ConcurrentQueue<(string Msisdn, string Reason)> Callbacks { get; } = new();
    private readonly ConcurrentDictionary<string, PaymentStarted> _byKey = new();

    private static readonly Dictionary<string, CustomerRef> Customers = new()
    {
        ["263772123456"] = new("IH-CUS-0001", "Tendai"),
        ["263733456789"] = new("IH-CUS-0003", "Farai"),
        ["263788112233"] = new("IH-CUS-0004", "Chipo"),
        ["263712987654"] = new("IH-CUS-0002", "Nyasha"),
        ["263774556677"] = new("IH-CUS-0005", "Tatenda"),
        ["263779000111"] = new("CUS-001001", "Mai Chipo"),
    };

    private static readonly Dictionary<string, PolicySummary[]> Policies = new()
    {
        ["IH-CUS-0001"] =
        [
            new("MOT-2026-000101", "MOTOR", "Toyota Hilux AEZ 4521", "ACTIVE", "USD", 94.50m, 0m),
            new("FUN-2026-000102", "FUNERAL", "Standard funeral plan", "ACTIVE", "USD", 18.00m, 0m),
        ],
        ["IH-CUS-0003"] = [new("MOT-2026-000301", "MOTOR", "HiAce Quantum ACF 7788", "LAPSED", "USD", 62.40m, 187.20m)],
        ["IH-CUS-0004"] = [new("FUN-2026-000401", "FUNERAL", "Premium funeral plan", "ACTIVE", "ZWG", 965.00m, 0m)],
        ["IH-CUS-0002"] = [new("HOM-2026-000201", "HOME", "Home cover Borrowdale", "ACTIVE", "USD", 71.25m, 0m)],
        ["IH-CUS-0005"] = [new("MOT-2026-000501", "MOTOR", "Honda Fit AFH 2290", "ACTIVE", "ZWG", 320.00m, 320.00m)],
    };

    private static readonly Dictionary<string, ClaimSummary[]> Claims = new()
    {
        ["IH-CUS-0001"] = [new("CLM-2026-000111", "UNDER REVIEW", "Assessor visit Thursday")],
        ["IH-CUS-0002"] = [new("CLM-2026-000211", "APPROVED", "Payment within 5 working days")],
        ["IH-CUS-0004"] = [new("CLM-2026-000411", "PAID", "Nothing further needed")],
        ["IH-CUS-0005"] = [new("CLM-2026-000511", "REJECTED", "Appeal in writing within 30 days")],
    };

    private async Task Simulate(CancellationToken ct)
    {
        if (Chaos.DelayMs > 0) await Task.Delay(Chaos.DelayMs, ct);
        if (Chaos.Down) throw new HttpRequestException("core system down (simulated)");
    }

    public async Task<CustomerRef?> FindCustomerAsync(string msisdn, CancellationToken ct)
    {
        await Simulate(ct);
        return Customers.GetValueOrDefault(msisdn);
    }

    public async Task<IReadOnlyList<PolicySummary>> PoliciesAsync(string customerRef, CancellationToken ct)
    {
        await Simulate(ct);
        return Policies.GetValueOrDefault(customerRef) ?? [];
    }

    public async Task<IReadOnlyList<ClaimSummary>> ClaimsAsync(string customerRef, CancellationToken ct)
    {
        await Simulate(ct);
        return Claims.GetValueOrDefault(customerRef) ?? [];
    }

    public async Task<IReadOnlyList<LoanSummary>> LoansAsync(string customerRef, CancellationToken ct)
    {
        await Simulate(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return customerRef == "CUS-001001"
            ? [new LoanSummary("lh-0001", "LN-2610-000001", "USD", 554.88m, 57.03m, today.AddDays(3), 0m)]
            : [];
    }

    public async Task<PaymentStarted> StartPaymentAsync(PaymentRequest request, CancellationToken ct)
    {
        await Simulate(ct);
        var started = _byKey.GetOrAdd(request.IdempotencyKey, _ =>
        {
            Payments.Enqueue(request);
            return new PaymentStarted($"PAY-{Payments.Count:D6}", "PENDING");
        });
        // the simulated customer approves the EcoCash prompt; the receipt comes by SMS
        _ = Task.Delay(1500, CancellationToken.None).ContinueWith(_ => sms.Send(request.Msisdn,
            $"InsureHub: {request.Currency} {request.Amount:0.00} received for {Mask.Reference(request.Reference)}. EcoCash ref MP{Random.Shared.Next(100000, 999999)}. Thank you."));
        return started;
    }

    public async Task RequestCallbackAsync(string msisdn, string? customerRef, string reason, CancellationToken ct)
    {
        await Simulate(ct);
        Callbacks.Enqueue((msisdn, reason));
    }
}
