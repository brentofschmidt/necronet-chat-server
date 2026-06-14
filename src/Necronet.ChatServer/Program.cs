using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using Necronet.ChatServer.Hubs;
using Necronet.ChatServer.Realtime;
using Necronet.ChatServer.Services;
using Necronet.Contracts;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ── Config sanity check ─────────────────────────────────────────────
// Surface missing required settings loudly at startup instead of as
// confusing 401s / connection errors at first use.
foreach (var (key, why) in new[]
{
    ("Supabase:Issuer",           "browser auth can't fetch signing keys and will reject every token"),
    ("ConnectionStrings:Supabase","channel membership checks will fail closed"),
    ("Internal:PublishSecret",    "the /internal/publish endpoint will reject every call"),
})
{
    if (string.IsNullOrWhiteSpace(config[key]))
        Console.WriteLine($"[startup] WARNING: '{key}' is not set — {why}.");
}

// ── Postgres (Supabase) ─────────────────────────────────────────────
// One shared, pooled data source. Lazily constructed so the app still
// boots if the connection string is absent (membership checks then
// fail closed and log).
builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(config.GetConnectionString("Supabase")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Supabase is not configured.")));
builder.Services.AddScoped<IChannelMembershipService, NpgsqlChannelMembershipService>();
// The write seam: Supabase-RPC-backed today, swappable for native
// service logic later without touching the hub.
builder.Services.AddScoped<IChatMessageService, SupabaseRpcChatMessageService>();

// ── Auth: validate Supabase-issued JWTs ─────────────────────────────
// Supabase signs user access tokens with the project's asymmetric JWT
// signing key (ES256), published at the project's JWKS endpoint. We
// point the handler at the project's OpenID metadata (Authority) so it
// fetches those keys and picks up key rotation automatically — the
// legacy HS256 shared secret is NOT used. The account id rides in the
// `sub` claim; we keep MapInboundClaims off so it stays the literal "sub".
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        // {Authority}/.well-known/openid-configuration → jwks_uri.
        // Issuer is e.g. https://<ref>.supabase.co/auth/v1.
        options.Authority = config["Supabase:Issuer"];
        options.RequireHttpsMetadata = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config["Supabase:Issuer"],
            ValidateAudience = true,
            ValidAudience = config["Supabase:Audience"] ?? "authenticated",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "sub",
        };

        // Browsers can't set an Authorization header on the WebSocket
        // handshake, so SignalR passes the token in the query string.
        // Pull it back out for hub requests.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            },

            // TEMP diagnostics: surface exactly why a token was rejected
            // (signature/issuer/audience/lifetime) instead of an opaque
            // 401. Remove once auth is confirmed working.
            OnAuthenticationFailed = ctx =>
            {
                Console.WriteLine(
                    $"[auth] FAILED ({ctx.Request.Path}): " +
                    $"{ctx.Exception.GetType().Name}: {ctx.Exception.Message}");
                return Task.CompletedTask;
            },
            OnChallenge = ctx =>
            {
                Console.WriteLine(
                    $"[auth] CHALLENGE ({ctx.Request.Path}): " +
                    $"error={ctx.Error}; desc={ctx.ErrorDescription}");
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// ── SignalR ─────────────────────────────────────────────────────────
// Single-instance for now. Going multi-instance later is the one-line
// `.AddStackExchangeRedis("<conn>")` backplane — no code change here.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubUserIdProvider>();

// The watchdog: LISTENs on Postgres 'chat_events' and fans DB-emitted
// notifications out to the right users. See ChatEventsListener.
builder.Services.AddHostedService<Necronet.ChatServer.Realtime.ChatEventsListener>();

// ── CORS for the browser client ─────────────────────────────────────
// Credentialed SignalR requires explicit origins (no wildcard).
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddPolicy("web", p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

// Liveness probe.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// The browser edge.
app.MapHub<ChatHub>("/hubs/chat");

// ── Internal publish seam ───────────────────────────────────────────
// Transport-neutral "fan this message out to its channel." Guarded by a
// shared secret, NOT the user JWT — this is the service-to-service door
// (web client post-send hook, a DB webhook, or the game server). When a
// Redis bus replaces this later, the contract (PublishMessageRequest)
// stays identical.
app.MapPost("/internal/publish", async (
    PublishMessageRequest req,
    HttpContext http,
    IHubContext<ChatHub, IChatClient> hub) =>
{
    var expected = config["Internal:PublishSecret"];
    var provided = http.Request.Headers["X-Internal-Secret"].ToString();
    if (string.IsNullOrEmpty(expected) || provided != expected)
        return Results.Unauthorized();

    await hub.Clients
        .Group(ChannelGroups.ForChannel(req.Message.ChannelId))
        .ReceiveMessage(req.Message);

    return Results.Accepted();
});

app.Run();
