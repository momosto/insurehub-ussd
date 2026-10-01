namespace Ussd.Gateway.Core;

/// <summary>The core systems behind the channel (principle P6: no business rules in channels).</summary>
public interface ICoreClient
{
    Task<CustomerRef?> FindCustomerAsync(string msisdn, CancellationToken ct);
    Task<IReadOnlyList<PolicySummary>> PoliciesAsync(string customerRef, CancellationToken ct);
    Task<IReadOnlyList<ClaimSummary>> ClaimsAsync(string customerRef, CancellationToken ct);
    Task<IReadOnlyList<LoanSummary>> LoansAsync(string customerRef, CancellationToken ct);
    Task<PaymentStarted> StartPaymentAsync(PaymentRequest request, CancellationToken ct);
    Task RequestCallbackAsync(string msisdn, string? customerRef, string reason, CancellationToken ct);
}

public sealed record CustomerRef(string Value, string FirstName);

public sealed record PolicySummary(string Number, string Line, string Description, string Status, string Currency,
    decimal MonthlyPremium, decimal Arrears);

public sealed record ClaimSummary(string Number, string Status, string NextStep);

public sealed record LoanSummary(string LoanId, string LoanNumber, string Currency, decimal Outstanding, decimal NextAmount,
    DateOnly? NextDueDate, decimal Arrears);

public sealed record PaymentRequest(string Kind, string Reference, decimal Amount, string Currency, string Msisdn,
    string CustomerRef, string IdempotencyKey);

public sealed record PaymentStarted(string PaymentId, string Status);

/// <summary>A core system timed out or failed and no cached copy was available.</summary>
public sealed class BackendUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Shared-phone safety (US-03): show only the start and the last 4 characters of references.</summary>
public static class Mask
{
    public static string Reference(string value)
    {
        var dash = value.IndexOf('-');
        return value.Length <= 6 ? value : $"{(dash > 0 ? value[..dash] : value[..3])}-..{value[^4..]}";
    }

    public static string Msisdn(string msisdn) => msisdn.Length < 4 ? msisdn : $"...{msisdn[^4..]}";
}
