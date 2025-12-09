namespace MockInterceptor.Sample;

public class Executor
{
    public static string GetVersion() => "v1";

    public virtual int Execute() => 1000;

    public unsafe int ExecuteUnsafe()
    {
        using var job = new Job();

        return *job.DoUnsafeWork();
    }
}
