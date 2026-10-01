using System.Diagnostics;
using Ussd.Gateway.Core;
using Ussd.Gateway.Handlers;
using Ussd.Gateway.Menus;
using Ussd.Gateway.Messaging;
using Ussd.Gateway.Observability;
using Ussd.Gateway.Sessions;

namespace Ussd.Gateway;

/// <summary>Aggregator callback fields (Africa's Talking style).</summary>
public sealed record UssdRequest(string SessionId, string PhoneNumber, string? ServiceCode, string? NetworkCode, string? Text);

/// <summary>One aggregator hop: load or start the session, apply the latest input, save, answer CON/END.</summary>
public sealed class UssdService(MenuRuntime runtime, ISessionStore store, Texts texts, SmsFallbackQueue fallback,
    UssdMetrics metrics, IConfiguration config, ILogger<UssdService> log)
{
    private TimeSpan SessionTtl => TimeSpan.FromSeconds(config.GetValue("Ussd:SessionSeconds", 180));

    public static string NormaliseMsisdn(string raw)
    {
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("00263")) digits = digits[2..];
        else if (digits.StartsWith('0')) digits = "263" + digits[1..];
        return digits;
    }

    public async Task<Screen> HandleAsync(UssdRequest request, CancellationToken ct)
    {
        using var activity = UssdMetrics.Activity.StartActivity("ussd.request");
        var started = Stopwatch.GetTimestamp();
        var msisdn = NormaliseMsisdn(request.PhoneNumber);
        UssdSession? session = null;
        try
        {
            session = await store.GetAsync(request.SessionId);
            // only the latest input matters; the history in `text` is never trusted or logged (it contains the PIN)
            var latest = string.IsNullOrEmpty(request.Text) ? null : request.Text.Split('*')[^1];
            Screen screen;
            if (session is null || latest is null)
            {
                session = new UssdSession
                {
                    SessionId = request.SessionId,
                    Msisdn = msisdn,
                    Language = await store.GetLanguageAsync(msisdn) ?? "en",
                };
                metrics.SessionStarted();
                screen = await runtime.StartAsync(session, ct);
            }
            else if (session.Msisdn != msisdn)
            {
                log.LogWarning("Session {SessionId} reused by a different number; rejected", request.SessionId);
                return Screen.End(texts.Get("en", "session.error"));
            }
            else
            {
                screen = await runtime.HandleAsync(session, latest, ct);
            }

            activity?.SetTag("ussd.node", session.Node);
            activity?.SetTag("ussd.language", session.Language);
            activity?.SetTag("network", request.NetworkCode);
            if (screen.Continue) await store.SaveAsync(session, SessionTtl);
            else await store.DeleteAsync(session.SessionId);
            return screen;
        }
        catch (BackendUnavailableException e)
        {
            metrics.Fallback();
            log.LogWarning("Backend unavailable at node {Node}: {Error}", session?.Node, e.Message);
            var what = session?.Node switch
            {
                { } n when n.StartsWith("policies") || n.StartsWith("pay") => "policies",
                { } n when n.StartsWith("claims") => "claims",
                { } n when n.StartsWith("loan") => "loans",
                _ => null,
            };
            if (what is not null && session?.CustomerRef is not null && !session.Node.EndsWith(".execute"))
            {
                fallback.Enqueue(new FallbackRequest(msisdn, session.CustomerRef, what, session.Language));
            }
            if (session is not null) await store.DeleteAsync(session.SessionId);
            return Screen.End(texts.Get(session?.Language ?? "en", "busy"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "USSD request failed at node {Node}", session?.Node);
            if (session is not null) await store.DeleteAsync(session.SessionId);
            return Screen.End(texts.Get(session?.Language ?? "en", "session.error"));
        }
        finally
        {
            metrics.Duration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
