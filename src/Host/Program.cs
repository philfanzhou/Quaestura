using System;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.QuestionBank.Database;
using Ruoyu.Study.QuestionBank.Database.Oss;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Ruoyu.Study.QuestionBank.Service;
using Ruoyu.Study.QuestionBank.Service.Mapping;
using Ruoyu.Study.QuestionBank.Service.Validation;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// 注册 Mapster 映射配置
MappingConfig.RegisterMappings();

// 获取数据库连接字符串（缺失则直接终止启动）
var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("数据库连接字符串 'Default' 未配置，请检查 appsettings.json 或环境变量。");

// 注册 EF Core DbContext
builder.Services.AddDbContext<QuestionBankDbContext>(options =>
    options.UseNpgsql(connectionString));

// 注册 MinIO OSS 服务（缺失关键配置则直接终止启动）
var ossEndpoint = config["Oss:Endpoint"] ?? throw new InvalidOperationException("OSS Endpoint 未配置，请检查 appsettings.json 或环境变量。");
var ossAccessKey = config["Oss:AccessKey"] ?? throw new InvalidOperationException("OSS AccessKey 未配置，请检查 appsettings.json 或环境变量。");
var ossSecretKey = config["Oss:SecretKey"] ?? throw new InvalidOperationException("OSS SecretKey 未配置，请检查 appsettings.json 或环境变量。");
var ossBucketName = config["Oss:BucketName"] ?? throw new InvalidOperationException("OSS BucketName 未配置，请检查 appsettings.json 或环境变量。");

var ossOptions = new OssOptions
{
    Endpoint = ossEndpoint,
    AccessKey = ossAccessKey,
    SecretKey = ossSecretKey,
    BucketName = ossBucketName
};
builder.Services.AddSingleton<IOssService>(new MinioOssService(
    ossOptions.Endpoint,
    ossOptions.AccessKey,
    ossOptions.SecretKey,
    ossOptions.BucketName));

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

// 自动执行数据库迁移
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<QuestionBankDbContext>();
    dbContext.Database.Migrate();
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
        logger.LogWarning("MinIO 连接失败，请检查 OSS 配置和 MinIO 服务状态");
    }
}

// 配置 Swagger
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "QuestionBank gRPC API v1"));

// 启用 gRPC-Web
app.UseGrpcWeb();

// 映射 gRPC 端点
app.MapGrpcService<QuestionBankServiceImpl>();

// 健康检查端点
app.MapGet("/health", () => "Healthy");

app.Run();
