using System;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.QuestionBank.Service.Validation;
using Xunit;

namespace QuestionBank.Test.Validation;

public class GrpcExceptionInterceptorTests
{
    private readonly Mock<ILogger<GrpcExceptionInterceptor>> _mockLogger;
    private readonly GrpcExceptionInterceptor _interceptor;

    public GrpcExceptionInterceptorTests()
    {
        _mockLogger = new Mock<ILogger<GrpcExceptionInterceptor>>();
        _interceptor = new GrpcExceptionInterceptor(_mockLogger.Object);
    }

    private static ServerCallContext CreateContext() => TestServerCallContextImpl.Create();

    [Fact]
    public async Task UnaryServerHandler_WhenContinuationSucceeds_ReturnsResult()
    {
        var expectedResponse = new TestResponse { Value = 42 };
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();
        continuationMock.Setup(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()))
            .ReturnsAsync(expectedResponse);

        var request = new TestRequest { Name = "test" };

        var response = await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        response.Should().BeSameAs(expectedResponse);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenContinuationThrowsRpcException_Rethrows()
    {
        var originalException = new RpcException(new Status(StatusCode.InvalidArgument, "validation failed"));
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();
        continuationMock.Setup(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()))
            .ThrowsAsync(originalException);

        var request = new TestRequest { Name = "test" };

        var act = async () => await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Be("validation failed");
        // 不应记录 RpcException 为错误日志
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenContinuationThrowsGenericException_ReturnsInternalError()
    {
        var innerException = new InvalidOperationException("database connection lost");
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();
        continuationMock.Setup(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()))
            .ThrowsAsync(innerException);

        var request = new TestRequest { Name = "test" };

        var act = async () => await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
        ex.Status.Detail.Should().Be("服务器内部错误，请联系管理员");
        // 应记录错误日志
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenContinuationThrowsGenericException_DoesNotLeakInternalDetails()
    {
        var innerException = new InvalidOperationException("sensitive internal info: connection string=...");
        var continuationMock = new Mock<UnaryServerMethod<TestRequest, TestResponse>>();
        continuationMock.Setup(c => c(It.IsAny<TestRequest>(), It.IsAny<ServerCallContext>()))
            .ThrowsAsync(innerException);

        var request = new TestRequest { Name = "test" };

        var act = async () => await _interceptor.UnaryServerHandler<TestRequest, TestResponse>(
            request, CreateContext(), continuationMock.Object);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.Status.Detail.Should().NotContain("sensitive");
        ex.Status.Detail.Should().NotContain("connection string");
        ex.Status.Detail.Should().Be("服务器内部错误，请联系管理员");
    }

    // 测试用的请求/响应类型
    public class TestRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public class TestResponse
    {
        public int Value { get; set; }
    }
}
