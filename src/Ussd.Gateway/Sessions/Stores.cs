using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace Ussd.Gateway.Sessions;

/// <summary>Server-side USSD session (ADR-0002). Only the latest input is read from the aggregator's <c>text</c>.</summary>
public sealed class UssdSession
{
    public required string SessionId { get; init; }
    public required string Msisdn { get; init; }
    public string Node { get; set; } = "";
    public string Language { get; set; } = "en";
    public string? CustomerRef { get; set; }
    public bool PinVerified { get; set; }
    public int Page { get; set; }
    public int Steps { get; set; }
    public Dictionary<string, string> Vars { get; set; } = new();
    public List<Dictionary<string, string>> ListItems { get; set; } = new();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface ISessionStore
{
    Task<UssdSession?> GetAsync(string sessionId);
    Task SaveAsync(UssdSession session, TimeSpan ttl);
    Task DeleteAsync(string sessionId);
    Task<string?> GetLanguageAsync(string msisdn);
    Task SetLanguageAsync(string msisdn, string language);
    /// <summary>Failed PIN attempts and lock state per number (TTL = lock window).</summary>
    Task<(int Attempts, DateTimeOffset? LockedUntil)> GetPinStateAsync(string msisdn);
    Task SetPinStateAsync(string msisdn, int attempts, DateTimeOffset? lockedUntil, TimeSpan ttl);
}

/// <summary>Argon2id PIN hashes. Kept apart from the volatile session store (no TTL).</summary>
public interface IPinStore
{
    Task<string?> GetHashAsync(string msisdn);
    Task SetHashAsync(string msisdn, string hash);
}

public sealed class InMemoryStores : ISessionStore, IPinStore
{
    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset Expires)> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _languages = new();
    private readonly ConcurrentDictionary<string, (int, DateTimeOffset?, DateTimeOffset)> _pinState = new();
    private readonly ConcurrentDictionary<string, string> _pins = new();
    private readonly TimeProvider _clock;

    public InMemoryStores(TimeProvider clock) => _clock = clock;

    public Task<UssdSession?> GetAsync(string sessionId) =>
        Task.FromResult(_sessions.TryGetValue(sessionId, out var s) && s.Expires > _clock.GetUtcNow()
            ? JsonSerializer.Deserialize<UssdSession>(s.Json) : null);

    public Task SaveAsync(UssdSession session, TimeSpan ttl)
    {
        _sessions[session.SessionId] = (JsonSerializer.Serialize(session), _clock.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<string?> GetLanguageAsync(string msisdn) => Task.FromResult(_languages.GetValueOrDefault(msisdn));

    public Task SetLanguageAsync(string msisdn, string language)
    {
        _languages[msisdn] = language;
        return Task.CompletedTask;
    }

    public Task<(int Attempts, DateTimeOffset? LockedUntil)> GetPinStateAsync(string msisdn) =>
        Task.FromResult(_pinState.TryGetValue(msisdn, out var s) && s.Item3 > _clock.GetUtcNow() ? (s.Item1, s.Item2) : (0, (DateTimeOffset?)null));

    public Task SetPinStateAsync(string msisdn, int attempts, DateTimeOffset? lockedUntil, TimeSpan ttl)
    {
        _pinState[msisdn] = (attempts, lockedUntil, _clock.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    public Task<string?> GetHashAsync(string msisdn) => Task.FromResult(_pins.GetValueOrDefault(msisdn));

    public Task SetHashAsync(string msisdn, string hash)
    {
        _pins[msisdn] = hash;
        return Task.CompletedTask;
    }
}

/// <summary>Redis keys from docs/03-architecture.md §4: ussd:s:{sessionId} (180 s), ussd:lang:{msisdn} (1 year), ussd:pin:{msisdn}.</summary>
public sealed class RedisStores : ISessionStore, IPinStore
{
    private readonly IDatabase _db;

    public RedisStores(IConnectionMultiplexer redis) => _db = redis.GetDatabase();

    public async Task<UssdSession?> GetAsync(string sessionId)
    {
        var raw = await _db.StringGetAsync($"ussd:s:{sessionId}");
        return raw.IsNullOrEmpty ? null : JsonSerializer.Deserialize<UssdSession>(raw.ToString());
    }

    public Task SaveAsync(UssdSession session, TimeSpan ttl) =>
        _db.StringSetAsync($"ussd:s:{session.SessionId}", JsonSerializer.Serialize(session), ttl);

    public Task DeleteAsync(string sessionId) => _db.KeyDeleteAsync($"ussd:s:{sessionId}");

    public async Task<string?> GetLanguageAsync(string msisdn)
    {
        var v = await _db.StringGetAsync($"ussd:lang:{msisdn}");
        return v.IsNullOrEmpty ? null : v.ToString();
    }

    public Task SetLanguageAsync(string msisdn, string language) =>
        _db.StringSetAsync($"ussd:lang:{msisdn}", language, TimeSpan.FromDays(365));

    public async Task<(int Attempts, DateTimeOffset? LockedUntil)> GetPinStateAsync(string msisdn)
    {
        var entries = await _db.HashGetAllAsync($"ussd:pin:{msisdn}");
        if (entries.Length == 0) return (0, null);
        var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        DateTimeOffset? locked = map.TryGetValue("lockedUntil", out var l) && long.TryParse(l, out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        return (int.Parse(map.GetValueOrDefault("attempts", "0")), locked);
    }

    public async Task SetPinStateAsync(string msisdn, int attempts, DateTimeOffset? lockedUntil, TimeSpan ttl)
    {
        var key = $"ussd:pin:{msisdn}";
        await _db.HashSetAsync(key, [new("attempts", attempts), new("lockedUntil", lockedUntil?.ToUnixTimeSeconds() ?? 0)]);
        await _db.KeyExpireAsync(key, ttl);
    }

    public async Task<string?> GetHashAsync(string msisdn)
    {
        var v = await _db.StringGetAsync($"ussd:pinhash:{msisdn}");
        return v.IsNullOrEmpty ? null : v.ToString();
    }

    public Task SetHashAsync(string msisdn, string hash) => _db.StringSetAsync($"ussd:pinhash:{msisdn}", hash);
}
