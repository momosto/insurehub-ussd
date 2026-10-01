using Microsoft.Extensions.Caching.Memory;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ussd.Gateway.Core;

/// <summary>The 2-second budget (architecture §5): every backend call gets 1.5 s and a circuit breaker. Reads keep a
/// last-good copy (10 minutes) that is served when the backend is slow or down; with no copy the caller gets
/// <see cref="BackendUnavailableException"/> and ends the session gracefully. Payments are never served from cache.</summary>
public sealed class ResilientCoreClient : ICoreClient
{
    private readonly ICoreClient _inner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ResilientCoreClient> _log;
    private readonly ResiliencePipeline _pipeline;
    private static readonly TimeSpan StaleFor = TimeSpan.FromMinutes(10);

    public ResilientCoreClient(ICoreClient inner, IMemoryCache cache, ILogger<ResilientCoreClient> log, TimeSpan? timeout = null)
    {
        _inner = inner;
        _cache = cache;
        _log = log;
        _pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(timeout ?? TimeSpan.FromMilliseconds(1500))
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5, MinimumThroughput = 4, SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is not OperationCanceledException || e is TimeoutRejectedException),
            })
            .Build();
    }

    private async Task<T> Read<T>(string key, Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        try
        {
            var value = await _pipeline.ExecuteAsync(async token => await call(token), ct);
            _cache.Set(key, value!, StaleFor);
            return value;
        }
        catch (Exception e) when (e is TimeoutRejectedException or BrokenCircuitException or HttpRequestException)
        {
            if (_cache.TryGetValue(key, out T? stale) && stale is not null)
            {
                _log.LogWarning("Serving cached {Key} after backend failure ({Error})", key.Split(':')[0], e.GetType().Name);
                return stale;
            }
            throw new BackendUnavailableException($"{key.Split(':')[0]} unavailable", e);
        }
    }

    public Task<CustomerRef?> FindCustomerAsync(string msisdn, CancellationToken ct) =>
        Read($"customer:{msisdn}", t => _inner.FindCustomerAsync(msisdn, t), ct);

    public Task<IReadOnlyList<PolicySummary>> PoliciesAsync(string customerRef, CancellationToken ct) =>
        Read($"policies:{customerRef}", t => _inner.PoliciesAsync(customerRef, t), ct);

    public Task<IReadOnlyList<ClaimSummary>> ClaimsAsync(string customerRef, CancellationToken ct) =>
        Read($"claims:{customerRef}", t => _inner.ClaimsAsync(customerRef, t), ct);

    public Task<IReadOnlyList<LoanSummary>> LoansAsync(string customerRef, CancellationToken ct) =>
        Read($"loans:{customerRef}", t => _inner.LoansAsync(customerRef, t), ct);

    public async Task<PaymentStarted> StartPaymentAsync(PaymentRequest request, CancellationToken ct)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async t => await _inner.StartPaymentAsync(request, t), ct);
        }
        catch (Exception e) when (e is TimeoutRejectedException or BrokenCircuitException or HttpRequestException)
        {
            // safe to retry later with the same idempotency key; nothing is charged twice
            throw new BackendUnavailableException("payments unavailable", e);
        }
    }

    public async Task RequestCallbackAsync(string msisdn, string? customerRef, string reason, CancellationToken ct)
    {
        try
        {
            await _pipeline.ExecuteAsync(async t => await _inner.RequestCallbackAsync(msisdn, customerRef, reason, t), ct);
        }
        catch (Exception e) when (e is TimeoutRejectedException or BrokenCircuitException or HttpRequestException)
        {
            throw new BackendUnavailableException("call-back service unavailable", e);
        }
    }
}
