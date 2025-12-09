# 🎣 MockInterceptor: Non-Intrusive Mocking for Coupled Code

`MockInterceptor` is a C# 12 Source Generator library designed to enable dependency mocking of tightly coupled classes. By strictly adhering to the **C# Interceptors method call specification**, it rewrites Intermediate Language (IL) at compile time, redirecting targeted method calls to a mock proxy managed by your test framework (e.g., Moq).

## 💡 The Problem Solved

In tightly coupled code, internal instantiation and static calls hinder unit testing:

```csharp
public class Worker
{
    private readonly Executor executor = new();

    public (string, int) DoWork() =>
        (Executor.GetVersion(), this.executor.Execute());    
}

```

`MockInterceptor` can bypasses the actual `Executor` code execution by transparently redirecting all method calls (`GetVersion`, `Execute`) to your mock setup.

---

## 🚀 Configuration: The Dedicated `Test` Build

To maintain a clean separation between development, testing, and production, we use a custom **`Test`** build configuration. This ensures the interception code is **only** compiled when running tests, avoiding overhead and maintaining a clean debugging experience for your regular `Debug` and `Release` builds.

### 1. Define the Custom `Test` Configuration

Ensure your solution file (`.slnx`) defines the `Test` build type alongside `Debug` and `Release`

```xml
<Configurations>
    <BuildType Name="Debug" />
    <BuildType Name="Test" /> 
    <BuildType Name="Release" />
</Configurations>

```

### 2. Configure the Target Project (`.csproj`)

The Target Project (containing `Worker` and `Executor`) must define the required constants and reference the generator *only* when the configuration is explicitly set to `Test`.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <InternalsVisibleTo Include="Your.Test.Project" />
  </PropertyGroup>

  <ItemGroup Condition="'$(Configuration)' == 'Test'">
    <PackageReference Include="MockInterceptor" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  
</Project>

```

---

## ✍️ Usage Guide

### 1. Marking the Target Class

Guard the `[Intercept]` assembly attribute with the `#if TEST` constant to ensure it's only included in your special test build.

**File: `AssemblyConfig.cs` (in the Target Project)**

```csharp
#if TEST
using MockInterceptor;

// Tells the generator to process the Executor class and all its method call sites.
[assembly: Intercept(typeof(Executor))] 
#endif

```

### 2. The Interception Mechanism Explained

The Source Generator emits interfaces and the static `ExecutorInterceptor` class to manage the mocks.

| Target Code | Interception Target | Mock Management Strategy |
| --- | --- | --- |
| **Static Call** (`Executor.GetVersion()`) | The `GetVersion()` method call. | Redirected to the **`StaticMock`** property (`IStaticExecutor`). |
| **Instance Call** (`executor.Execute()`) | The `Execute()` method call. | Redirected to a generated **Extension Method** that checks the mock state. |
| **Constructor** (`new Executor()`) | **No Direct Interception.** | The mock (from **`MockQueue`**) is *consumed and tracked* by the **first instance method call** (`Execute()`) on the newly created object. |

### 3. Writing the TestIn your Test Project, use Moq with the generated interfaces and control the redirection via the `ExecutorInterceptor`'s static properties

```csharp
// Guarding the test class with #if TEST because ExecutorInterceptor is generated only in the test build
#if TEST
using MockInterceptor.Interceptors; // Generated namespace
using Moq;
using Xunit;

namespace MockInterceptor.Sample.Tests;

public class InterceptorTest
{
    [Fact]
    public void MockStaticAndInstance()
    {
        // 1. Reset state (Essential for test isolation)
        ExecutorInterceptor.Reset();

        // 2. Create Mocks using the generated interfaces
        var instanceMock = new Mock<IExecutor>();
        var staticMock   = new Mock<IStaticExecutor>();

        // 3. Setup mock expectations
        instanceMock.Setup(x => x.Execute()).Returns(42);
        staticMock.Setup(x => x.GetVersion()).Returns("v5");

        // --- STAGING THE MOCKS ---

        // 4. Stage the Static Mock
        ExecutorInterceptor.StaticMock = staticMock.Object; 
        
        // 5. Queue the Instance Mock (Consumed by the first instance method call)
        ExecutorInterceptor.MockQueue.Enqueue(instanceMock.Object);

        // 6. Act (Worker instantiation and calls trigger the Interceptors)
        var worker = new Worker();
        var actual = worker.DoWork(); 

        // 7. Assert
        var expected = ("v5", 42);
        Assert.Equal(expected, actual);
        
        // Verify the instance mock was called
        instanceMock.Verify(x => x.Execute(), Times.Once);
    }
}
#endif

```
