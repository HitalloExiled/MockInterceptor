namespace MockInterceptor.Sample;

public class Worker
{
    private readonly Executor executor = new();

    public (string, int) DoWork() =>
        (Executor.GetVersion(), this.executor.Execute());

    public int DoUnsafeWork() =>
        this.executor.ExecuteUnsafe();
}
