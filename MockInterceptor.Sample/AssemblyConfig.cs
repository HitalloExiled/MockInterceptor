
#if TEST
using MockInterceptor.Sample;

[assembly: MockInterceptor.Intercept(typeof(Executor))]
[assembly: MockInterceptor.Intercept(typeof(Job))]
#endif
