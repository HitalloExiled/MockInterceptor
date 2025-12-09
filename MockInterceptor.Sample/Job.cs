using System.Runtime.InteropServices;

namespace MockInterceptor.Sample;

public unsafe class Job : IDisposable
{
    private readonly int* allocation = (int*)NativeMemory.Alloc(sizeof(int));
    private bool disposed;

    ~Job()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
            }

            NativeMemory.Free(this.allocation);

            this.disposed = true;
        }
    }

    public int* DoUnsafeWork()
    {
        *this.allocation = 100;

        return this.allocation;
    }

    public void Dispose()
    {
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
