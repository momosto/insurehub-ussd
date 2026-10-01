using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ussd.Gateway.Core;
using Ussd.Gateway.Messaging;

namespace Ussd.Gateway.Tests;

/// <summary>The real app (in-memory stores, fixture core systems) with optional overrides and a log capture.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string Secret = "test-ussd-secret";
    public readonly List<string> Logs = new();
    private readonly Action<IServiceCollection>? _services;
    private readonly Dictionary<string, string?> _settings;

    public TestApp(Action<IServiceCollection>? services = null, Dictionary<string, string?>? settings = null)
    {
        _services = services;
        _settings = new()
        {
            ["Ussd:CallbackSecret"] = Secret,
            ["Ussd:FallbackRetrySeconds"] = "0",
            ["Core:TimeoutMs"] = "400",
            ["Simulator:Enabled"] = "true",
            ["Security:RecentSimSwaps:0"] = "263712987654",
        };
        foreach (var (k, v) in settings ?? new()) _settings[k] = v;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        builder.ConfigureLogging(l => l.AddProvider(new CaptureProvider(Logs)));
        builder.ConfigureTestServices(s => _services?.Invoke(s));
    }

    public FixtureCoreClient Fixtures => Services.GetRequiredService<FixtureCoreClient>();
    public SimulatedSmsSender Sms => Services.GetRequiredService<SimulatedSmsSender>();

    public Phone Phone(string msisdn) => new(this, msisdn);

    private sealed class CaptureProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(sink);
        public void Dispose() { }

        private sealed class Capture(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink) sink.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>Plays the aggregator for one handset: keeps the session ID and the '*'-joined input history.</summary>
public sealed class Phone(TestApp app, string msisdn)
{
    private readonly HttpClient _http = app.CreateClient();
    private readonly List<string> _inputs = new();
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    public string Msisdn => msisdn;

    public async Task<string> Dial()
    {
        SessionId = Guid.NewGuid().ToString("N");
        _inputs.Clear();
        return await Send(null);
    }

    public Task<string> Reply(string input) => Send(input);

    private async Task<string> Send(string? input)
    {
        if (input is not null) _inputs.Add(input);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ussd")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["sessionId"] = SessionId, ["phoneNumber"] = "+" + msisdn, ["serviceCode"] = "*263#", ["networkCode"] = "64804",
                ["text"] = string.Join("*", _inputs),
            }),
        };
        request.Headers.Add("X-Ussd-Secret", TestApp.Secret);
        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public string LatestOtp()
    {
        var sms = app.Sms.For(msisdn).Last(m => m.Text.Contains("PIN is") || m.Text.Contains("ndeiyi") || m.Text.Contains("ngu-"));
        return Regex.Match(sms.Text, @"(?<!\d)(\d{6})(?!\d)").Groups[1].Value;
    }

    /// <summary>Dials, chooses a main-menu option and sets up PIN 4826 via OTP (first use) or enters it.</summary>
    public async Task<string> SignIn(string mainOption, string pin = "4826")
    {
        await Dial();
        var screen = await Reply(mainOption);
        if (screen.Contains("6-digit"))
        {
            await Reply(LatestOtp());
            await Reply(pin);
            return await Reply(pin);
        }
        return await Reply(pin);
    }
}
