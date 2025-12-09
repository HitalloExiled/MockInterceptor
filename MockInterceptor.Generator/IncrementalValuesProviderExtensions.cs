using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace MockInterceptor.Generator;

internal static class IncrementalValuesProviderExtensions
{
    public static IncrementalValuesProvider<T> WhereNotNull<T>(this IncrementalValuesProvider<T?> provider) where T : class
    {
        provider = provider.Where(static x => x != null);

        return Unsafe.As<IncrementalValuesProvider<T?>, IncrementalValuesProvider<T>>(ref provider);
    }
}
