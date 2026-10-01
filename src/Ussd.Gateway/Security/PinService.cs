using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Ussd.Gateway.Sessions;

namespace Ussd.Gateway.Security;

public enum PinCheck { Ok, Wrong, Locked }

/// <summary>4-digit PINs (US-02): Argon2id hashes, weak-PIN rules, 3 wrong attempts → 30-minute lock.</summary>
public sealed class PinService
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(30);

    private readonly IPinStore _pins;
    private readonly ISessionStore _state;
    private readonly TimeProvider _clock;

    public PinService(IPinStore pins, ISessionStore state, TimeProvider clock)
    {
        _pins = pins;
        _state = state;
        _clock = clock;
    }

    public async Task<bool> HasPinAsync(string msisdn) => await _pins.GetHashAsync(msisdn) is not null;

    /// <summary>Rejects 0000-style repeats, ascending/descending runs (1234, 9876) and years 1940–2029.</summary>
    public static bool IsWeak(string pin)
    {
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) return true;
        if (pin.Distinct().Count() == 1) return true;
        var d = pin.Select(c => c - '0').ToArray();
        bool run(int step) => Enumerable.Range(1, 3).All(i => d[i] - d[i - 1] == step);
        if (run(1) || run(-1)) return true;
        var asNumber = int.Parse(pin);
        return asNumber is >= 1940 and <= 2029;
    }

    public async Task SetPinAsync(string msisdn, string pin)
    {
        if (IsWeak(pin)) throw new ArgumentException("weak PIN");
        await _pins.SetHashAsync(msisdn, Hash(pin));
        await _state.SetPinStateAsync(msisdn, 0, null, LockFor);
    }

    public async Task<(PinCheck Result, int AttemptsLeft)> VerifyAsync(string msisdn, string pin)
    {
        var (attempts, lockedUntil) = await _state.GetPinStateAsync(msisdn);
        if (lockedUntil is { } until && until > _clock.GetUtcNow()) return (PinCheck.Locked, 0);
        var stored = await _pins.GetHashAsync(msisdn);
        if (stored is not null && Verify(pin, stored))
        {
            await _state.SetPinStateAsync(msisdn, 0, null, LockFor);
            return (PinCheck.Ok, MaxAttempts);
        }
        attempts++;
        if (attempts >= MaxAttempts)
        {
            await _state.SetPinStateAsync(msisdn, attempts, _clock.GetUtcNow() + LockFor, LockFor);
            return (PinCheck.Locked, 0);
        }
        await _state.SetPinStateAsync(msisdn, attempts, null, LockFor);
        return (PinCheck.Wrong, MaxAttempts - attempts);
    }

    public async Task<bool> IsLockedAsync(string msisdn)
    {
        var (_, lockedUntil) = await _state.GetPinStateAsync(msisdn);
        return lockedUntil is { } until && until > _clock.GetUtcNow();
    }

    // Argon2id (OWASP minimum: m=19 MiB, t=2, p=1). Format: argon2id$m$t$p$salt$hash
    internal static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return $"argon2id$19456$2$1${Convert.ToBase64String(salt)}${Convert.ToBase64String(Derive(pin, salt, 19456, 2, 1))}";
    }

    internal static bool Verify(string pin, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 6 || p[0] != "argon2id") return false;
        var expected = Convert.FromBase64String(p[5]);
        var actual = Derive(pin, Convert.FromBase64String(p[4]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Derive(string pin, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(pin))
        {
            Salt = salt, MemorySize = memoryKb, Iterations = iterations, DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(32);
    }
}

/// <summary>SIM-swap check before payments (simulated MNO API). Numbers swapped in the last 72 h may not pay by USSD.</summary>
public interface ISimSwapChecker
{
    Task<bool> SwappedRecentlyAsync(string msisdn);
}

public sealed class ConfiguredSimSwapChecker(IConfiguration config) : ISimSwapChecker
{
    public Task<bool> SwappedRecentlyAsync(string msisdn) =>
        Task.FromResult((config.GetSection("Security:RecentSimSwaps").Get<string[]>() ?? []).Contains(msisdn));
}
