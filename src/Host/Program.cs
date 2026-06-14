using System;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
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
using Ruoyu.Study.QuestionBank.Service.Mapping;
using Ruoyu.Study.QuestionBank.Service.Validation;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// 注册 Mapster 映射配置
MappingConfig.RegisterMappings();

// 获取数据库连接字符串（缺失则直接终止启动）
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
    ossOptions.Endpoint, ossOptions.AccessKey, ossOptions.SecretKey, ossOptions.BucketName));

// 注册领域服务
builder.Services.AddScoped<IQuestionService, QuestionService>();
builder.Services.AddScoped<IKnowledgeService, KnowledgeService>();
builder.Services.AddScoped<IQuestionKnowledgeService, QuestionKnowledgeService>();
builder.Services.AddScoped<IOssQuestionService, OssQuestionService>();

// 注册 gRPC 服务（含验证拦截器和全局异常拦截器）
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = true;
    options.Interceptors.Add<GrpcValidationInterceptor>();
    options.Interceptors.Add<GrpcExceptionInterceptor>();
});

// 注册 FluentValidation 验证器
builder.Services.AddValidatorsFromAssemblyContaining<QuestionValidator>();

// 注册 gRPC 拦截器
builder.Services.AddScoped<GrpcValidationInterceptor>();
builder.Services.AddScoped<GrpcExceptionInterceptor>();

// 注册内存缓存
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1024;
});

// 添加 Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "QuestionBank gRPC API", Version = "v1" });
});

var app = builder.Build();

app.Logger.LogInformation("QuestionBank Service starting");
app.Logger.LogInformation("Listening: {Urls}", Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(default)");
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

// 自动执行数据库迁移
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseInitializer.InitializeAsync(dbContext, logger);
}

// MinIO 连通性检测
using (var scope = app.Services.CreateScope())
{
    var ossService = scope.ServiceProvider.GetRequiredService<IOssService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var connected = await ossService.CheckConnectivityAsync();
    if (connected)
    {
        logger.LogInformation("MinIO 连接正常");
    }
    else
    {
        logger.LogWarning("S3 存储连接失败，请检查 OSS 配置和存储服务状态");
    }
}

// 配置 Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "QuestionBank gRPC API v1"));
}

app.UseGrpcWeb();

// 映射 gRPC 端点
app.MapGrpcService<QuestionBankServiceImpl>();

// 健康检查端点
app.MapGet("/health", () => "Healthy");

app.Run();
