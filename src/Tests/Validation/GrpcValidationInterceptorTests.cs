using System;
using System.Threading.Tasks;
using FluentAssertions;
using FluentValidation;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Ruoyu.Study.QuestionBank.Service.Validation;
using Xunit;

namespace QuestionBank.Test.Validation;

public class GrpcValidationInterceptorTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly GrpcValidationInterceptor _interceptor;

    public GrpcValidationInterceptorTests()
    {
        var services = new ServiceCollection();
        services.AddValidatorsFromAssemblyContaining<QuestionValidator>();
        services.AddValidatorsFromAssemblyContaining<TestRequestValidator>();
        _serviceProvider = services.BuildServiceProvider();
        _interceptor = new GrpcValidationInterceptor(_serviceProvider);
    }

    private static ServerCallContext CreateContext() => TestServerCallContextImpl.Create();

    [Fact]
    public async Task UnaryServerHandler_WithNullRequest_ThrowsInvalidArgument()
    {
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();

        var act = async () => await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            null!, CreateContext(), continuationMock.Object);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("Request cannot be null");
        continuationMock.Verify(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()), Times.Never);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenNoValidatorRegistered_CallsContinuation()
    {
        // TestRequest2 has no registered validator
        var continuationMock = new Mock<UnaryServerMethod<TestRequest2, TestResponse>>();
        var expectedResponse = new TestResponse { Value = 42 };
        continuationMock.Setup(c => c(It.IsAny<TestRequest2>(), It.IsAny<ServerCallContext>()))
            .ReturnsAsync(expectedResponse);

        var request = new TestRequest2 { Name = "test" };

        var response = await _interceptor.UnaryServerHandler<TestRequest2, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        response.Should().BeSameAs(expectedResponse);
        continuationMock.Verify(c => c(request, It.IsAny<ServerCallContext>()), Times.Once);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenValidationPasses_CallsContinuation()
    {
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();
        var expectedResponse = new TestResponse { Value = 100 };
        continuationMock.Setup(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()))
            .ReturnsAsync(expectedResponse);

        var request = new TestRequest { Name = "valid-name" };

        var response = await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        response.Should().BeSameAs(expectedResponse);
        continuationMock.Verify(c => c(request, It.IsAny<ServerCallContext>()), Times.Once);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenValidationFails_ThrowsInvalidArgument()
    {
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();

        var request = new TestRequest { Name = "" }; // 空名称会触发验证失败

        var act = async () => await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("名称不能为空");
        continuationMock.Verify(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()), Times.Never);
    }

    // 测试用的请求/响应类型和验证器
    public class TestRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public class TestRequest2
    {
        public string Name { get; set; } = string.Empty;
    }

    public class TestResponse
    {
        public int Value { get; set; }
    }

    public class TestRequestValidator : AbstractValidator<TestRequest>
    {
        public TestRequestValidator()
        {
            RuleFor(x => x.Name).NotEmpty().WithMessage("名称不能为空");
        }
    }
}
