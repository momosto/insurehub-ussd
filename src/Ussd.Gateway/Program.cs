using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Ussd.Gateway;
using Ussd.Gateway.Core;
using Ussd.Gateway.Handlers;
using Ussd.Gateway.Menus;
using Ussd.Gateway.Messaging;
using Ussd.Gateway.Observability;
using Ussd.Gateway.Security;
using Ussd.Gateway.Sessions;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var services = builder.Services;

// ---------------------------------------------------------------------------------------------- menus & texts
var contentRoot = AppContext.BaseDirectory;
var menu = MenuDefinition.Load(Path.Combine(contentRoot, "data", "menus", "main.json"));
var texts = Texts.Load(Path.Combine(contentRoot, "data", "i18n"));
services.AddSingleton(menu);
services.AddSingleton(texts);
services.AddSingleton(TimeProvider.System);
services.AddMemoryCache();
services.AddMetrics();
services.AddSingleton<UssdMetrics>();

// ---------------------------------------------------------------------------------------------- state
var redis = config["Redis:ConnectionString"];
if (string.IsNullOrWhiteSpace(redis))
{
    services.AddSingleton<InMemoryStores>();
    services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<InMemoryStores>());
    services.AddSingleton<IPinStore>(sp => sp.GetRequiredService<InMemoryStores>());
}
else
{
    services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
    services.AddSingleton<RedisStores>();
    services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<RedisStores>());
    services.AddSingleton<IPinStore>(sp => sp.GetRequiredService<RedisStores>());
}
services.AddSingleton<PinService>();
services.AddSingleton<ISimSwapChecker, ConfiguredSimSwapChecker>();

// ---------------------------------------------------------------------------------------------- messaging
services.AddSingleton<SimulatedSmsSender>();
services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<SimulatedSmsSender>());
services.AddSingleton<SmsFallbackQueue>();
services.AddHostedService<SmsFallbackWorker>();

// ---------------------------------------------------------------------------------------------- core systems
var coreOptions = config.GetSection("Core").Get<CoreOptions>() ?? new CoreOptions();
services.AddSingleton(coreOptions);
var timeout = TimeSpan.FromMilliseconds(config.GetValue("Core:TimeoutMs", 1500));
if (config.GetValue("Core:Mode", "fixtures") == "http")
{
    services.AddHttpClient("insurehub", c => c.BaseAddress = new Uri(coreOptions.InsureHubUrl));
    services.AddHttpClient("lendhub", c => c.BaseAddress = new Uri(coreOptions.LendHubUrl));
    services.AddHttpClient("payments", c => c.BaseAddress = new Uri(coreOptions.PaymentsUrl));
    services.AddHttpClient("insureassist", c => c.BaseAddress = new Uri(coreOptions.InsureAssistUrl));
    services.AddSingleton<HttpCoreClient>();
    services.AddSingleton<ICoreClient>(sp => new ResilientCoreClient(sp.GetRequiredService<HttpCoreClient>(),
        sp.GetRequiredService<IMemoryCache>(), sp.GetRequiredService<ILogger<ResilientCoreClient>>(), timeout));
}
else
{
    services.AddSingleton<FixtureCoreClient>();
    services.AddSingleton<ICoreClient>(sp => new ResilientCoreClient(sp.GetRequiredService<FixtureCoreClient>(),
        sp.GetRequiredService<IMemoryCache>(), sp.GetRequiredService<ILogger<ResilientCoreClient>>(), timeout));
}

// ---------------------------------------------------------------------------------------------- handlers & engine
services.AddSingleton<IMenuHandler, PoliciesHandler>();
services.AddSingleton<IMenuHandler>(new SelectedItemHandler("PolicyDetail"));
services.AddSingleton<IMenuHandler>(new SelectedItemHandler("PremiumAmount"));
services.AddSingleton<IMenuHandler, ClaimsHandler>();
services.AddSingleton<IMenuHandler, LoanSummaryHandler>();
services.AddSingleton<IMenuHandler>(sp => new StartPaymentHandler("StartPremiumPayment", "premium",
    sp.GetRequiredService<ISimSwapChecker>(), config));
services.AddSingleton<IMenuHandler>(sp => new StartPaymentHandler("StartLoanPayment", "loan",
    sp.GetRequiredService<ISimSwapChecker>(), config));
services.AddSingleton<IMenuHandler, RequestCallbackHandler>();
services.AddSingleton<IMenuHandler, SetLanguageHandler>();
services.AddSingleton<MenuRuntime>();
services.AddSingleton<UssdService>();

// ---------------------------------------------------------------------------------------------- observability
var otlp = config["Otel:Endpoint"];
services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("insurehub-ussd"))
    .WithTracing(t =>
    {
        t.AddSource(UssdMetrics.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp)) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    })
    .WithMetrics(m =>
    {
        m.AddMeter(UssdMetrics.Name).AddAspNetCoreInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp)) m.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    });

var app = builder.Build();

// Fail fast on a broken menu: unknown targets, dead ends, missing translations, unregistered handlers.
var errors = MenuValidator.Validate(menu, texts, app.Services.GetRequiredService<MenuRuntime>().HandlerNames);
if (errors.Count > 0) throw new InvalidOperationException("Invalid menu: " + string.Join("; ", errors));

static IResult Respond(Screen screen) =>
    Results.Text((screen.Continue ? "CON " : "END ") + screen.Text, "text/plain", Encoding.UTF8);

static async Task<UssdRequest?> ReadForm(HttpRequest http)
{
    if (!http.HasFormContentType) return null;
    var f = await http.ReadFormAsync();
    var sessionId = f["sessionId"].ToString();
    var phone = f["phoneNumber"].ToString();
    return string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(phone) ? null
        : new UssdRequest(sessionId, phone, f["serviceCode"], f["networkCode"], f["text"]);
}

// Aggregator callback: shared-secret header + optional IP allowlist (docs/04).
app.MapPost("/ussd", async (HttpContext http, UssdService ussd, CancellationToken ct) =>
{
    var secret = config["Ussd:CallbackSecret"] ?? "";
    var given = http.Request.Headers["X-Ussd-Secret"].ToString();
    var allowed = config.GetSection("Ussd:AllowedIps").Get<string[]>() ?? [];
    var ipOk = allowed.Length == 0 || (http.Connection.RemoteIpAddress is { } ip && allowed.Any(a => IPAddress.Parse(a).Equals(ip)));
    if (!ipOk || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(secret)))
        return Results.Unauthorized();
    var request = await ReadForm(http.Request);
    return request is null ? Results.BadRequest() : Respond(await ussd.HandleAsync(request, ct));
}).DisableAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "UP" }));
app.MapGet("/health/ready", async (ISessionStore store) =>
{
    await store.GetAsync("readiness-probe");
    return Results.Ok(new { status = "UP", core = config.GetValue("Core:Mode", "fixtures") });
});
app.MapGet("/stats", (UssdMetrics metrics) => Results.Ok(metrics.Snapshot()));

// Phone simulator for demos (never enabled with a real aggregator).
if (config.GetValue("Simulator:Enabled", false))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapPost("/simulator/ussd", async (HttpContext http, UssdService ussd, CancellationToken ct) =>
    {
        var request = await ReadForm(http.Request);
        return request is null ? Results.BadRequest() : Respond(await ussd.HandleAsync(request, ct));
    }).DisableAntiforgery();
    app.MapGet("/simulator/sms", (string msisdn, SimulatedSmsSender sms) =>
        Results.Ok(sms.For(UssdService.NormaliseMsisdn(msisdn)).Select(m => new { at = m.At, text = m.Text })));
    app.MapPost("/simulator/chaos", (int? delayMs, bool? down, IServiceProvider sp) =>
    {
        var fixtures = sp.GetService<FixtureCoreClient>();
        if (fixtures is null) return Results.BadRequest("chaos only applies to fixture mode");
        if (delayMs is not null) fixtures.Chaos.DelayMs = delayMs.Value;
        if (down is not null) fixtures.Chaos.Down = down.Value;
        return Results.Ok(fixtures.Chaos);
    });
}

app.Run();

public partial class Program;
