using System.Collections.Concurrent;
using System.Threading.Channels;
using Ussd.Gateway.Core;

namespace Ussd.Gateway.Messaging;

/// <summary>SMS out (OTPs, receipts, fallbacks). The demo uses an in-memory outbox the phone simulator displays;
/// production would call the SMS aggregator or the Notifications service.</summary>
public interface ISmsSender
{
    void Send(string msisdn, string text);
}

public sealed class SimulatedSmsSender(TimeProvider clock, ILogger<SimulatedSmsSender> log) : ISmsSender
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(DateTimeOffset At, string Text)>> _outbox = new();

    public void Send(string msisdn, string text)
    {
        _outbox.GetOrAdd(msisdn, _ => new()).Enqueue((clock.GetUtcNow(), text));
        log.LogInformation("SMS queued for ...{Last4} ({Length} chars)", msisdn[^4..], text.Length); // never the content (OTPs)
    }

    public IReadOnlyList<(DateTimeOffset At, string Text)> For(string msisdn) =>
        _outbox.TryGetValue(msisdn, out var q) ? q.ToList() : [];
}

/// <summary>"We're busy, we'll SMS you" (architecture §5): retries the read after the session has ended and sends
/// the answer by SMS.</summary>
public sealed record FallbackRequest(string Msisdn, string CustomerRef, string What, string Language);

public sealed class SmsFallbackQueue
{
    private readonly Channel<FallbackRequest> _channel = Channel.CreateBounded<FallbackRequest>(1000);
    public ChannelReader<FallbackRequest> Reader => _channel.Reader;
    public bool Enqueue(FallbackRequest request) => _channel.Writer.TryWrite(request);
}

public sealed class SmsFallbackWorker(SmsFallbackQueue queue, IServiceProvider services, ISmsSender sms,
    ILogger<SmsFallbackWorker> log, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(config.GetValue("Ussd:FallbackRetrySeconds", 20));
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                    var core = services.GetRequiredService<ICoreClient>();
                    var details = request.What switch
                    {
                        "policies" => string.Join("; ", (await core.PoliciesAsync(request.CustomerRef, stoppingToken))
                            .Select(p => $"{Mask.Reference(p.Number)} {p.Status} arrears {p.Currency} {p.Arrears:0.00}")),
                        "claims" => string.Join("; ", (await core.ClaimsAsync(request.CustomerRef, stoppingToken))
                            .Select(c => $"{Mask.Reference(c.Number)} {c.Status}")),
                        "loans" => string.Join("; ", (await core.LoansAsync(request.CustomerRef, stoppingToken))
                            .Select(l => $"{Mask.Reference(l.LoanNumber)} balance {l.Currency} {l.Outstanding:0.00}, next {l.NextAmount:0.00} on {l.NextDueDate:yyyy-MM-dd}")),
                        _ => "",
                    };
                    if (details.Length > 140) details = details[..138] + ".."; // one SMS segment
                    sms.Send(request.Msisdn, $"InsureHub: {(details.Length == 0 ? "nothing to show" : details)}");
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Fallback SMS attempt {Attempt} failed: {Error}", attempt, e.GetType().Name);
                }
            }
        }
    }
}
