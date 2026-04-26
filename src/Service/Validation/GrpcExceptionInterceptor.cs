using System;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.QuestionBank.Service.Validation;

/// <summary>
/// gRPC 服务端全局异常拦截器，统一捕获未处理异常并记录日志。
/// 对外返回脱敏后的错误信息，避免泄露内部实现细节。
/// </summary>
public class GrpcExceptionInterceptor : Interceptor
{
    private readonly ILogger<GrpcExceptionInterceptor> _logger;

    public GrpcExceptionInterceptor(ILogger<GrpcExceptionInterceptor> logger)
    {
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (RpcException)
        {
            // gRPC 原生异常（如 ValidationInterceptor 抛出的 InvalidArgument）直接重新抛出
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC 方法 {Method} 执行时发生未处理异常，请求类型: {RequestType}",
                context.Method, typeof(TRequest).Name);

            // 对外返回脱敏错误信息，不泄露内部异常细节
            throw new RpcException(new Status(StatusCode.Internal, "服务器内部错误，请联系管理员"));
        }
    }
}
