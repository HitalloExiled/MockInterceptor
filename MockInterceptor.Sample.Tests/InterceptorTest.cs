#if TEST
using System.Runtime.InteropServices;
using MockInterceptor.Interceptors;

namespace MockInterceptor.Sample.Tests;

file class JobDouble : IJob
{
    public unsafe delegate int* DoUnsafeWorkHandle();

    public Action? DisposeImpl { get; set; }
    public DoUnsafeWorkHandle? DoUnsafeWorkImpl { get; set; }
    public void Dispose() => this.DisposeImpl?.Invoke();
    public unsafe int* DoUnsafeWork() => this.DoUnsafeWorkImpl != null ? this.DoUnsafeWorkImpl.Invoke() : default;
}

public class InterceptorTest
{
    [Fact]
    public void MockStaticAndInstance()
    {
        ExecutorInterceptor.Reset();

        var executorMock       = new Mock<IExecutor>();
        var staticExecutorMock = new Mock<IStaticExecutor>();

        executorMock.Setup(x => x.Execute()).Returns(42);
        staticExecutorMock.Setup(x => x.GetVersion()).Returns("v5");

        ExecutorInterceptor.StaticMock = staticExecutorMock.Object;
        ExecutorInterceptor.MockQueue.Enqueue(executorMock.Object);

        var worker = new Worker();

        var expected = ("v5", 42);
        var actual   = worker.DoWork();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public unsafe void MockUnsafe()
    {
        JobInterceptor.Reset();

        var jobMock = new JobDouble();

        JobInterceptor.MockQueue.Enqueue(jobMock);

        var p = (int*)NativeMemory.Alloc(sizeof(int));

        const int EXPECTED = 10000;

        *p = EXPECTED;

        jobMock.DoUnsafeWorkImpl = () => p;

        var worker = new Worker();

        var actual = worker.DoUnsafeWork();

        Assert.Equal(EXPECTED, actual);

        NativeMemory.Free(p);
    }
}
#endif
