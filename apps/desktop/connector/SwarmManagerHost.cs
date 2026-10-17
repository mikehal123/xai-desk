using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sovereign.Desktop.Connector;

/// <summary>Loopback MCP server for the in-app swarm manager. Not the Grok Bot connector on port 8790.</summary>
public sealed class SwarmManagerHost : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly SemaphoreSlim Gate = new(1, 1);
    static SwarmManagerHost? _shared;
    readonly string? _root;
    readonly string _token;
    WebApplication? _app;

    SwarmManagerHost(string? root)
    {
        _root = root;
        _token = ConnectorToken.Create();
    }

    public string? Url { get; private set; }
    public string Token => _token;

    public static async Task<SwarmManagerHost> SharedAsync(CancellationToken ct = default)
    {
        if (_shared is { Url: not null }) return _shared;
        await Gate.WaitAsync(ct);
        try
        {
            if (_shared is { Url: not null }) return _shared;
            _shared = await StartAsync(null, ct);
            return _shared;
        }
        finally { Gate.Release(); }
    }

    public static async Task<SwarmManagerHost> StartAsync(string? root, CancellationToken ct = default)
    {
        var host = new SwarmManagerHost(root);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(SwarmManagerHost).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 262144;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        app.MapPost("/mcp", host.HandleAsync);
        try { await app.StartAsync(ct); }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            throw new IOException("Swarm manager tools could not start. " + ConnectorBind.OneLine(ex.Message), ex);
        }
        var port = BoundPort(app);
        if (port is null)
        {
            await app.DisposeAsync();
            throw new IOException("Swarm manager tools did not bind a loopback port.");
        }
        host._app = app;
        host.Url = "http://127.0.0.1:" + port + "/mcp";
        if (host.Url.Contains(":8790", StringComparison.Ordinal))
        {
            await host.DisposeAsync();
            throw new IOException("Swarm mode does not use the Grok Bot connector.");
        }
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        var app = _app;
        _app = null;
        Url = null;
        if (app is null) return;
        try { await app.DisposeAsync(); }
        catch (Exception) { }
    }

    async Task HandleAsync(HttpContext context)
    {
        if (!ConnectorBind.IsLoopbackV4(context.Connection.LocalIpAddress ?? IPAddress.None))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var reason = ConnectorToken.RejectReason(_token, context.Request.Headers.Authorization.ToString());
        if (reason is not null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var message = await context.Request.ReadFromJsonAsync<JsonElement>(Json, context.RequestAborted);
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out var methodElement))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var method = methodElement.GetString() ?? "";
        var hasId = message.TryGetProperty("id", out var id);
        var result = SwarmManagerMcp.Handle(method, message, _root);
        if (!hasId || result is null) return;
        await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result }, Json, context.RequestAborted);
    }

    static int? BoundPort(WebApplication app)
    {
        foreach (var item in app.Urls)
            if (Uri.TryCreate(item, UriKind.Absolute, out var uri) && uri.Port > 0 && uri.Host is "127.0.0.1" or "localhost")
                return uri.Port;
        return null;
    }
}
