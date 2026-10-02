using System;
using System.IO;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaestura.Common.Authentication;
using Quaestura.Common.Oss;
using Quaestura.Consul;
using Quaestura.Database;
using Quaestura.Domain.Services;
using Quaestura.Host;
using Quaestura.Service;
using Quaestura.Service.Endpoints;
using Quaestura.Service.Options;
using Quaestura.Service.Validation;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using ServiceMantle.Web.Logging;

var builder = WebApplication.CreateBuilder(args);

// ========== Consul Configuration Source ==========
builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);
var consulOptions = RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = RuoyuConsulRuntimeState.Instance;

// ========== ServiceMantle logging (sanitizing Console + optional Grafana Loki) ==========
// The single shared logging entry point: mandatory structured sanitization, Console always on,
// and the remote Loki sink enabled only by a non-empty Loki:Uri. Structured identity comes from
// the ServiceMantle registration below (ServiceLogContext scopes), and the fixed Loki stream
// label stays service=Quaestura.
builder.AddQuaesturaLogging();

var config = builder.Configuration;

// HTTP listen port is hardcoded to 5007 (not configurable via ASPNETCORE_URLS).
// host port mapping is controlled by start.sh: -p ${Port}:5007.
const int httpPort = 5007;

// Get database connection string (Consul-shared PostgreSQL with local fallback)
var connectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(
    builder.Configuration,
    builder.Configuration.GetConnectionString("Default"));
builder.Services.AddDbContext<QuaesturaDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

var ossOptions = config.GetSection("Oss").Get<OssOptions>() ?? new OssOptions();
builder.Services.AddSingleton<IOssService>(new S3OssService(ossOptions));

// Register domain services
builder.Services.AddScoped<IQuestionService, QuestionService>();
builder.Services.AddScoped<IKnowledgeService, KnowledgeService>();
builder.Services.AddScoped<IQuestionKnowledgeService, QuestionKnowledgeService>();
builder.Services.AddScoped<ITagService, TagService>();
builder.Services.AddScoped<IQuestionTagService, QuestionTagService>();
builder.Services.AddScoped<IOssQuestionService, OssQuestionService>();

// Register FluentValidation validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateQuestionRequestValidator>();

// Register memory cache (reserved for future use)
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1024;
});

// JWT Bearer authentication uses the shared IdentityService trust contract.
// FallbackPolicy = RequireAuthenticatedUser, so every /admin/* endpoint requires
// a valid JWT unless explicitly decorated with [AllowAnonymous].
builder.Services.AddRuoyuJwtBearer(config, builder.Environment);

// Admin login: SignaCore client credentials and the admin role whitelist.
builder.Services.Configure<IdentityServiceClientOptions>(
    config.GetSection(IdentityServiceClientOptions.SectionName));
builder.Services.Configure<AdminPortalOptions>(config.GetSection(AdminPortalOptions.SectionName));
builder.Services.AddHttpClient(
    IdentityServiceClientOptions.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(30));

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Quaestura WebAPI", Version = "v1" });
});

// ========== ServiceMantle (host identity, correlation, base telemetry) ==========
// ServiceId "quaestura" is the stable deployment identity. The InstanceId is regenerated on
// every host build and is NOT a persistent identity: it changes on each restart. No bootstrap
// file path is passed: the Bootstrap store stays a lazy singleton and this wiring performs zero
// disk writes. No service version is passed: it resolves from the entry assembly informational
// version. AddOpenTelemetryInstrumentation uses the default options (AspNetCore / HttpClient /
// Runtime instrumentation) and registers NO exporter. AddSensitiveHeaders denies
// X-Admin-AppSecret for the safe request-header projector without adding header logging.
// AddSecurityResponseHeaders registers the opt-in capability that applies the fixed six-header
// baseline to the two admin-auth JSON endpoints marked in AdminAuthEndpoints (the middleware
// below is endpoint-metadata driven; the full management pipeline is deliberately not used).
builder.Services.AddQuaesturaServiceMantle();

// Bind Kestrel explicitly to the configured httpPort
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort);
});

var app = builder.Build();

// First middleware in the HTTP pipeline: everything registered below (Swagger, the Problem
// Details boundary, the SPA branch, authentication, every endpoint, and /health) runs inside the
// request correlation scope and receives the x-correlation-id response header, injected via
// OnStarting before any response starts (static file responses included). The Problem Details
// middleware sits inside this scope so problem bodies and logs share the same correlation id.
app.UseServiceMantleCorrelationId();

// ========== Security response headers for the marked admin-auth JSON endpoints ==========
// Explicit routing selects the endpoint here (WebApplication would otherwise auto-prepend it),
// so the endpoint-metadata-driven security middleware below can see the selected endpoint. It
// sits outside the Problem Details branch on purpose: when a marked endpoint throws and the
// boundary converts it into an application/problem+json response, the OnStarting
// assignment still applies the fixed single-value six-header baseline. Unmarked endpoints
// (business API, /health, dev Swagger, the SPA branch, static assets) pass through untouched
// and never receive the API-only default-src 'none' policy. If the response has already
// started, the middleware passes through without promising to rewrite sent headers.
app.UseRouting();
app.UseServiceMantleSecurityResponseHeaders();

var identityTrust = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityAuthenticationOptions>>()
    .Value;

var appTitle = config["APP_TITLE"] ?? "Quaestura.Admin";

// Normal startup logs run inside the shared structured identity scope (ServiceName,
// ServiceVersion, InstanceId) resolved from the ServiceMantle registration; the former local
// pipeline's global enrichers (MachineName/ThreadId/WithProperty identity) are gone. HTTP
// request logs receive the same identity fields plus CorrelationId from the correlation
// middleware's request scope. The scope covers startup diagnostics, database initialization,
// and the OSS connectivity check only; it ends before the server starts accepting requests.
var logContext = app.Services.GetRequiredService<ServiceLogContext>();
using (logContext.BeginScope(app.Logger))
{
    app.Logger.LogInformation("Quaestura Service starting");
    app.Logger.LogInformation(
        "Identity trust: Authority={Authority}, Issuers={Issuers}, Audience={Audience}, RequireHttpsMetadata={RequireHttpsMetadata}",
        identityTrust.Authority,
        string.Join(",", identityTrust.GetValidIssuers()),
        identityTrust.Audience,
        identityTrust.RequireHttpsMetadata);
    app.Logger.LogInformation(
        "Consul startup diagnostics: Address={Address}, Token={Token}, Source={Source}, KeyCount={KeyCount}, Prefixes={Prefixes}, LastError={LastError}",
        $"{consulOptions.Host}:{consulOptions.Port}",
        StartupDiagnosticsFormatter.MaskSecret(consulOptions.Token),
        consulRuntimeState.Source,
        consulRuntimeState.KeyCount,
        StartupDiagnosticsFormatter.SummarizePrefixes(consulRuntimeState.LoadedPrefixes),
        StartupDiagnosticsFormatter.SummarizeError(consulRuntimeState.LastError));
    app.Logger.LogInformation("Listening: http://+:{Port}", httpPort);
    app.Logger.LogInformation(
        "Effective configuration diagnostics: PostgreSqlHost={PostgreSqlHost}, PostgreSqlPort={PostgreSqlPort}, PostgreSqlUsername={PostgreSqlUsername}, PostgreSqlPassword={PostgreSqlPassword}, DatabaseName={DatabaseName}",
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Host"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Port"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Username"]),
        StartupDiagnosticsFormatter.SummarizePassword(builder.Configuration["PostgreSql:Password"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Database:Name"]));
    app.Logger.LogInformation("OSS: {Endpoint}/{Bucket}", ossOptions.InternalEndpoint, ossOptions.BucketName);
    app.Logger.LogInformation("APP_TITLE: {Title}", appTitle);

    // Complete the shared gate before the OSS check and before accepting HTTP requests.
    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
        var receipt = app.Services.GetRequiredService<StartupDatabaseReceipt>();
        var stopping = app.Lifetime.ApplicationStopping;
        if (dbContext.Database.IsRelational())
        {
            StartupDatabaseGateOptions options;
            try { options = QuaesturaStartupDatabaseOptions.Create(config, connectionString); }
            catch (InvalidOperationException)
            {
                receipt.TryMarkRunning();
                receipt.TryCompleteFailed(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget);
                throw;
            }
            var result = await app.Services.GetRequiredService<StartupDatabaseGate>().RunAsync(
                options, receipt, ServiceId.Parse(ServiceMantleComposition.ServiceIdValue), stopping);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Database startup gate failed ({result.ErrorCode}); refusing to start.");
            app.Logger.LogInformation("Database startup gate completed (executor was called: {ExecutorWasCalled})",
                result.ExecutorWasCalled);
        }
        else
        {
            receipt.TryMarkRunning();
            try
            {
                await dbContext.Database.EnsureCreatedAsync(stopping);
                stopping.ThrowIfCancellationRequested();
                receipt.TryCompleteSucceeded();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
            catch
            {
                receipt.TryCompleteFailed(WellKnownMigrationErrorCodes.ExecutionFailed);
                throw;
            }
        }
    }

    // OSS connectivity check
    if (!app.Environment.IsEnvironment("Testing"))
    {
        using var ossScope = app.Services.CreateScope();
        var ossService = ossScope.ServiceProvider.GetRequiredService<IOssService>();
        var logger = ossScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var connected = await ossService.CheckConnectivityAsync();
        if (connected)
        {
            logger.LogInformation("OSS connection OK");
        }
        else
        {
            logger.LogWarning("S3 storage connection failed, check OSS configuration");
        }
    }
}

// Configure Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Quaestura WebAPI v1"));
}

// Safe Problem Details boundary for the /admin JSON API only: the UseWhen branch runs for
// every request whose path starts with the /admin segment (every business JSON endpoint and
// both admin-auth endpoints), so /health, the SPA branch, static assets, and dev Swagger never
// enter it and keep their existing contracts. The exception mappings themselves are registered
// in ServiceMantleComposition (per exact exception type, plus ordered conditional candidates
// for the DomainException base type and InvalidOperationException); unmapped exceptions fall
// through to the library's fixed safe 500 with errorCode http.internal_server_error and the
// response always carries the request correlation id. The branch builder is an independent
// IApplicationBuilder instance, so this composition does not conflict with the library's
// PipelineComposition rules for UseServiceMantlePipeline. The security response-header
// middleware stays outside the branch and writes its six headers via OnStarting, so problem
// responses of marked endpoints carry the same single-value baseline.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/admin"),
    branch => branch.UseServiceMantleProblemDetails());

// ========== Static files & SPA for Admin Web (HTTP port only) ==========
// Serves Vue 3 frontend SPA built into wwwroot/.
// Excludes /admin (API), /health, /swagger so the API still works.
// Registered before authentication on purpose: the admin frontend (including its login page)
// must load without a token, otherwise signed-out users could never sign in.
// Requests handled by this branch never reach UseAuthentication/UseAuthorization.
var escapedAppTitle = appTitle.Replace("'", "\\'");

// Injects APP_TITLE into index.html and writes the response.
// Shared by the /index.html request and the SPA history fallback so that every
// response returning index.html content (/, /index.html, and any client-route
// deep link) goes through the same injection. Returns false when index.html is
// missing so callers can fall through to the previous static-file behavior.
async Task<bool> TryWriteInjectedIndexHtml(HttpContext context)
{
    var wwwroot = app.Environment.WebRootPath;
    var filePath = Path.Combine(wwwroot ?? string.Empty, "index.html");
    if (!File.Exists(filePath))
    {
        return false;
    }

    var content = await File.ReadAllTextAsync(filePath);
    content = content.Replace("__APP_TITLE__", appTitle);
    content = content.Replace(
        "</head>",
        $"<script>window.__APP_TITLE__ = '{escapedAppTitle}';</script></head>");
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(content);
    return true;
}

app.MapWhen(
    context => !context.Request.Path.StartsWithSegments("/admin")
             && !context.Request.Path.StartsWithSegments("/health")
             && !context.Request.Path.StartsWithSegments("/swagger"),
    spaApp =>
    {
        spaApp.UseDefaultFiles();

        // Inject app title from APP_TITLE env var into index.html at runtime.
        spaApp.Use(async (context, next) =>
        {
            if (context.Request.Path == "/index.html" && await TryWriteInjectedIndexHtml(context))
            {
                return;
            }
            await next();
        });

        spaApp.UseStaticFiles();

        // SPA fallback for Vue Router history mode: serve the same injected
        // index.html as /index.html so deep links (e.g. /login, /questions/123)
        // do not leak the raw __APP_TITLE__ placeholder.
        spaApp.MapWhen(_ => true, innerSpa =>
        {
            innerSpa.Use(async (context, next) =>
            {
                if (await TryWriteInjectedIndexHtml(context))
                {
                    return;
                }
                context.Request.Path = "/index.html";
                await next();
            });
            innerSpa.UseStaticFiles();
        });
    });

// Authentication & authorization (JWT Bearer)
app.UseAuthentication();
app.UseAuthorization();

// Map endpoints
app.MapQuestionEndpoints();
app.MapKnowledgeEndpoints();
app.MapQuestionKnowledgeEndpoints();
app.MapTagEndpoints();
app.MapQuestionTagEndpoints();
app.MapAdminAuthEndpoints();

// ========== ServiceMantle health endpoints (anonymous) ==========
// The library maps GET /health/live (always 200 while the endpoint executes, never resolving
// the snapshot source), GET /health/ready, and GET /health (the readiness alias replacing the
// former fixed 200 "Healthy" response — a deliberate public contract change). The routes carry
// no authorization metadata, so mapping them inside an anonymous route group exempts exactly
// these endpoints while the FallbackPolicy keeps protecting every /admin endpoint. The SPA
// branch above excludes the whole /health path segment, so the JSON probes are never swallowed
// by the fallback. Readiness fails closed: it requires the recorded real initialization
// success plus this request's own read-only database evidence.
var healthEndpoints = app.MapGroup(string.Empty).AllowAnonymous();
healthEndpoints.MapServiceMantleHealthEndpoints();

app.Run();

public partial class Program;
