using System;
using System.Data.Common;
using System.IO;
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
using Quaestura.Service;
using Quaestura.Service.Endpoints;
using Quaestura.Service.Middleware;
using Quaestura.Service.Validation;

var builder = WebApplication.CreateBuilder(args);

// ========== Consul Configuration Source ==========
builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);
var consulOptions = RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = RuoyuConsulRuntimeState.Instance;

// ========== Serilog (Console + Grafana Loki) ==========
builder.Configuration.AddRuoyuLokiSink();
builder.Host.UseRuoyuSerilog("Quaestura");

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

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Quaestura WebAPI", Version = "v1" });
});

// Bind Kestrel explicitly to the configured httpPort
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort);
});

var app = builder.Build();
var identityTrust = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityAuthenticationOptions>>()
    .Value;

var appTitle = config["APP_TITLE"] ?? "Quaestura.Admin";

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
if (!string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
app.Logger.LogInformation(
    "Effective configuration diagnostics: PostgreSqlHost={PostgreSqlHost}, PostgreSqlPort={PostgreSqlPort}, PostgreSqlUsername={PostgreSqlUsername}, PostgreSqlPassword={PostgreSqlPassword}, DatabaseName={DatabaseName}",
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Host"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Port"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Username"]),
    StartupDiagnosticsFormatter.SummarizePassword(builder.Configuration["PostgreSql:Password"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Database:Name"]));
app.Logger.LogInformation("OSS: {Endpoint}/{Bucket}", ossOptions.InternalEndpoint, ossOptions.BucketName);
app.Logger.LogInformation("APP_TITLE: {Title}", appTitle);

// Apply database initialization
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    if (dbContext.Database.IsRelational())
    {
        await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
    }
    else
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
}

// OSS connectivity check
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var ossService = scope.ServiceProvider.GetRequiredService<IOssService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
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

// Configure Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Quaestura WebAPI v1"));
}

// Global exception handling
app.UseMiddleware<ExceptionHandlingMiddleware>();

// ========== Static files & SPA for Admin Web (HTTP port only) ==========
// Serves Vue 3 frontend SPA built into wwwroot/.
// Excludes /admin (API), /health, /swagger so the API still works.
// Registered before authentication on purpose: the admin frontend (including its login page)
// must load without a token, otherwise signed-out users could never sign in.
// Requests handled by this branch never reach UseAuthentication/UseAuthorization.
var escapedAppTitle = appTitle.Replace("'", "\\'");
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
            if (context.Request.Path == "/index.html")
            {
                var wwwroot = app.Environment.WebRootPath;
                var filePath = Path.Combine(wwwroot ?? string.Empty, "index.html");
                if (File.Exists(filePath))
                {
                    var content = await File.ReadAllTextAsync(filePath);
                    content = content.Replace("__APP_TITLE__", appTitle);
                    content = content.Replace(
                        "</head>",
                        $"<script>window.__APP_TITLE__ = '{escapedAppTitle}';</script></head>");
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(content);
                    return;
                }
            }
            await next();
        });

        spaApp.UseStaticFiles();

        // SPA fallback for Vue Router history mode
        spaApp.MapWhen(_ => true, innerSpa =>
        {
            innerSpa.Use(async (context, next) =>
            {
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

// Health check (public, no auth required)
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();

app.Run();

public partial class Program;
