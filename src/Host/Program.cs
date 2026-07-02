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
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.QuestionBank.Database;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Ruoyu.Study.QuestionBank.Service;
using Ruoyu.Study.QuestionBank.Service.Endpoints;
using Ruoyu.Study.QuestionBank.Service.Middleware;
using Ruoyu.Study.QuestionBank.Service.Validation;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// HTTP port for the only listener (single-port deployment: API + SPA on 5007)
var httpPort = 5007;

// Get database connection string (missing = fail to start)
var connectionString = config.GetConnectionString("Default");
var isPostgreSql = !string.IsNullOrWhiteSpace(connectionString)
    && (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase));
builder.Services.AddDbContext<QuestionBankDbContext>(options =>
{
    if (isPostgreSql)
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString ?? "Data Source=data/sqlite/ruoyu_study_questionbank.db");
});

var ossOptions = config.GetSection("Oss").Get<OssOptions>() ?? new OssOptions();
builder.Services.AddSingleton<IOssService>(new S3OssService(
    ossOptions.Endpoint, ossOptions.AccessKey, ossOptions.SecretKey, ossOptions.BucketName,
    publicEndpoint: ossOptions.PublicEndpoint));

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

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "QuestionBank WebAPI", Version = "v1" });
});

// Bind Kestrel explicitly to the configured httpPort
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort);
});

var app = builder.Build();

var appTitle = config["APP_TITLE"] ?? "Ruoyu.Study.QuestionBank.Admin";

app.Logger.LogInformation("QuestionBank Service starting");
app.Logger.LogInformation("Listening: http://+:{Port}", httpPort);
if (isPostgreSql && !string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
else
{
    app.Logger.LogInformation("Database: SQLite");
}
app.Logger.LogInformation("OSS: {Endpoint}/{Bucket}", ossOptions.Endpoint, ossOptions.BucketName);
app.Logger.LogInformation("APP_TITLE: {Title}", appTitle);

// Apply database initialization
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
}

// OSS connectivity check
using (var scope = app.Services.CreateScope())
{
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
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "QuestionBank WebAPI v1"));
}

// Global exception handling
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Map endpoints
app.MapQuestionEndpoints();
app.MapKnowledgeEndpoints();
app.MapQuestionKnowledgeEndpoints();
app.MapTagEndpoints();
app.MapQuestionTagEndpoints();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

// ========== Static files & SPA for Admin Web (HTTP port only) ==========
// Serves Vue 3 questionbank_portal/frontend SPA built into wwwroot/.
// Excludes /admin (API), /health, /swagger so the API still works.
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

app.Run();
