using System;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;

namespace Ruoyu.Study.QuestionBank.Service.Validation;

/// <summary>
/// gRPC 服务端拦截器，自动对请求执行 FluentValidation 验证。
/// 验证失败的请求被拦截并返回 INVALID_ARGUMENT 状态码。
/// </summary>
public class GrpcValidationInterceptor : Interceptor
{
    private readonly IServiceProvider _serviceProvider;

    public GrpcValidationInterceptor(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (request == null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request cannot be null"));
        }

        var requestType = request.GetType();
        var validatorType = typeof(IValidator<>).MakeGenericType(requestType);

        // 查找已注册的验证器
        using var scope = _serviceProvider.CreateScope();
        var validator = scope.ServiceProvider.GetService(validatorType) as IValidator;

        if (validator != null)
        {
            var validationContext = new ValidationContext<TRequest>(request);
            var validationResult = await validator.ValidateAsync(validationContext);

            if (!validationResult.IsValid)
            {
                var errors = string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage));
                throw new RpcException(new Status(StatusCode.InvalidArgument, errors));
            }
        }

        return await continuation(request, context);
    }
}
