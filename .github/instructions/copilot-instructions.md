# GitHub Copilot Coding Agent Instructions

## Language and runtime

- **C# 13 / .NET 10** throughout. Do not introduce preview features unless explicitly asked.
- Target `net10.0` in all `.csproj` files.
- Enable `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in every project. All nullable warnings are build errors.
- Use file-scoped namespaces (`namespace MyProject.Core;` not the braced form).
- Use primary constructors where they reduce noise, but not when they obscure intent (e.g. complex DI graphs with many dependencies).
- Prefer `record` types for immutable data transfer objects; prefer `class` for anything with behaviour or mutable state.
- Use collection expressions (`[1, 2, 3]`, `[..a, ..b]`) in preference to `new List<T> { }` or `Array.Empty<T>()`.
- Async all the way down: every I/O call must be `async`/`await`. Never use `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` except at a documented top-level entry point.
- Pass `CancellationToken` through every async call chain. Accept it as the last parameter on all public async methods.

---

## SOLID principles

These are non-negotiable. Every PR is reviewed against them.

### Single Responsibility
Each class has one reason to change. If a class is doing orchestration, parsing, persistence, *and* logging — split it. Services that grow beyond ~200 lines are a warning sign.

### Open/Closed
Extend behaviour through new implementations, not by modifying existing classes. Use interfaces and abstractions so new variants can be added without touching existing code.

```csharp
// ✅ New backend = new class, nothing else changes
public class NewBackend : IBackend { ... }

// ❌ Adding a case to a switch in an existing class
switch (type) { case "new": ... }
```

### Liskov Substitution
Implementations must be substitutable for their abstractions without changing correctness. Do not throw `NotImplementedException` on interface members — if a contract can't be fulfilled, the abstraction is wrong.

### Interface Segregation
Prefer narrow, focused interfaces over broad ones. If a consumer only needs two methods from a ten-method interface, split the interface. Don't force implementations to stub out methods they don't need.

```csharp
// ✅ Focused
public interface IReader { Task<T> ReadAsync(...); }
public interface IWriter { Task WriteAsync(...); }

// ❌ Broad — forces implementors to stub half the members
public interface IRepository { Read(); Write(); Delete(); Exists(); Count(); Paginate(); ... }
```

### Dependency Inversion
High-level modules must not depend on low-level modules. Both depend on abstractions. All dependencies are injected via constructor — never `new`ed inside business logic.

```csharp
// ✅ Depends on abstraction
public class OrderService(IPaymentGateway gateway, ILogger<OrderService> logger) { ... }

// ❌ Depends on concrete
public class OrderService() { _gateway = new StripeGateway(); }
```

---

## Best practices

### Design
- **Composition over inheritance.** Inherit only for genuine is-a relationships. Prefer injecting collaborators.
- **Immutability by default.** Make fields `readonly`, properties `init`-only, and collections immutable (`IReadOnlyList<T>`, `FrozenDictionary<K,V>`) unless mutation is explicitly required.
- **Fail fast.** Validate inputs at the boundary (constructor or method entry). Use `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIfNegative`, etc. Don't let bad data propagate silently.
- **Guard clauses over nested ifs.** Return or throw early; keep the happy path un-indented.

```csharp
// ✅ Guard clause
public async Task ProcessAsync(Order order, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(order);
    if (!order.IsValid) throw new InvalidOrderException(order.Id);

    await _processor.ProcessAsync(order, ct);
}
```

### Naming
- Types, methods, and properties: `PascalCase`. Local variables and parameters: `camelCase`. Private fields: `_camelCase`.
- Names describe intent, not implementation. `IMessageRouter` not `IMessageHandler2`. `ProcessPaymentAsync` not `DoStuff`.
- Boolean properties and variables: use `is`, `has`, `can` prefixes (`isReady`, `hasErrors`, `canRetry`).
- Avoid abbreviations. `configuration` not `cfg`. `cancellationToken` not `ct` in public APIs (fine in private/local scope).

### Dependency injection
Register in a dedicated `IServiceCollection` extension method per project layer, not directly in `Program.cs`.

```csharp
// ✅ Extension method per layer
public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
{
    services.AddSingleton<IEventBus, InMemoryEventBus>();
    services.AddScoped<IOrderRepository, PostgresOrderRepository>();
    return services;
}
```

- `AddSingleton` for stateless, thread-safe services.
- `AddScoped` for per-request state (web apps) or per-operation state (workers).
- `AddTransient` only for lightweight, truly stateless objects with no shared resources.

### Configuration
Use `IOptions<T>` with strongly-typed config classes. Bind from `appsettings.json` plus environment variable overrides. Never inject `IConfiguration` into business logic — only into infrastructure bootstrap code.

```csharp
public class DatabaseOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public int CommandTimeoutSeconds { get; init; } = 30;
}
```

### Logging
Use `ILogger<T>` from `Microsoft.Extensions.Logging`. Always use structured logging with named placeholders — never string interpolation.

```csharp
// ✅ Structured — fields are queryable in log aggregators
_logger.LogInformation("Order {OrderId} processed in {ElapsedMs}ms", order.Id, elapsed.TotalMilliseconds);

// ❌ Interpolation — loses structured fields
_logger.LogInformation($"Order {order.Id} processed in {elapsed.TotalMilliseconds}ms");
```

Log levels:
- `Trace` — verbose internal state, disabled in production
- `Debug` — diagnostic detail useful during development
- `Information` — significant state transitions (order placed, job completed)
- `Warning` — recoverable issues, degraded behaviour
- `Error` — failures that affect correctness; always include the exception object

### Error handling
- Define typed exception classes for distinct failure modes; derive from a project-base exception where appropriate.
- Catch at the pipeline/application boundary. Do not scatter try/catch through infrastructure internals.
- Never swallow exceptions silently. Log and rethrow, or convert to a structured error result.
- Use `Result<T>` / discriminated union patterns for expected failures (validation, not-found). Reserve exceptions for unexpected/exceptional conditions.

### Performance
- Prefer `ValueTask` over `Task` for hot-path async methods that frequently complete synchronously.
- Use `IAsyncEnumerable<T>` for streaming result sets rather than returning `List<T>` of unbounded size.
- Avoid LINQ in hot paths — profile before optimising, but be intentional.
- Dispose `IDisposable` / `IAsyncDisposable` resources promptly; use `await using` for async disposables.

---

## Project conventions

### Solution structure
Follow a layered structure. Layer dependencies flow inward only — inner layers never reference outer layers.

```
src/
  MyProject.Core/           # Domain: entities, value objects, domain services, interfaces
  MyProject.Application/    # Use cases, orchestration, application services
  MyProject.Infrastructure/ # Persistence, external APIs, messaging, file I/O
  MyProject.Api/            # ASP.NET Core host, controllers / minimal API endpoints
tests/
  MyProject.Core.Tests/
  MyProject.Application.Tests/
  MyProject.Infrastructure.Tests/
  MyProject.Api.Tests/
  MyProject.IntegrationTests/
```

### XML documentation
All `public` and `protected` types and members must have XML doc comments. Summaries should describe *what* and *why*, not restate the name.

```csharp
/// <summary>
/// Routes an inbound request to the appropriate handler based on its type and context.
/// Returns null if no handler is registered for the given request type.
/// </summary>
public Task<IHandler?> ResolveAsync(RequestContext context, CancellationToken ct);
```

---

## Testing

- Use **NUnit** for all tests.
- Use **NSubstitute** for substituting interfaces — do not use Moq.
- Test project naming: `<Project>.Tests` for unit tests, `<Project>.IntegrationTests` for integration tests.
- Test method naming: `MethodName_StateUnderTest_ExpectedBehaviour` (e.g. `ProcessAsync_WithNullOrder_ThrowsArgumentNullException`).
- Every public method should have at minimum: a happy-path test, a null/invalid-input test, and a failure/edge-case test.
- Keep tests **DAMP** (Descriptive And Meaningful Phrases), not DRY — duplication in tests is acceptable if it improves readability.
- Use `[TestCase]` and `[TestCaseSource]` for parametrised inputs rather than copy-paste test methods.
- Use `[SetUp]` to construct the system under test and its substitutes. Name the SUT field `_sut` consistently.

```csharp
[TestFixture]
public class OrderServiceTests
{
    private IPaymentGateway _gateway;
    private OrderService _sut;

    [SetUp]
    public void SetUp()
    {
        _gateway = Substitute.For<IPaymentGateway>();
        _sut = new OrderService(_gateway);
    }

    [Test]
    public async Task ProcessAsync_WithValidOrder_CallsGatewayOnce()
    {
        var order = OrderFixtures.ValidOrder();

        await _sut.ProcessAsync(order, CancellationToken.None);

        await _gateway.Received(1).ChargeAsync(order.Total, Arg.Any<CancellationToken>());
    }

    [Test]
    public void ProcessAsync_WithNullOrder_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.ProcessAsync(null!, CancellationToken.None));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ProcessAsync_WithNonPositiveAmount_ThrowsArgumentOutOfRangeException(decimal amount)
    {
        var order = OrderFixtures.OrderWithAmount(amount);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _sut.ProcessAsync(order, CancellationToken.None));
    }
}
```

Integration tests that require real infrastructure (database, message broker, external APIs) are excluded from the default run via a category attribute:

```csharp
[TestFixture]
[Category("Integration")]
public class OrderRepositoryIntegrationTests { ... }
```

Run unit tests only (default CI):
```bash
dotnet test --filter "Category!=Integration"
```

Run integration tests explicitly:
```bash
dotnet test --filter "Category=Integration"
```

---

## Docker and deployment

- Each runnable project has its own `Dockerfile` using multi-stage builds.
- Base images: `mcr.microsoft.com/dotnet/aspnet:10.0` for runtime, `mcr.microsoft.com/dotnet/sdk:10.0` for build.
- All containers run as a non-root user. Add `USER app` after the publish stage.
- `docker-compose.yml` at the repo root wires services together. Environment-specific overrides go in `docker-compose.override.yml` (gitignored).
- Never hardcode hostnames, ports, or credentials in source or Dockerfiles — use environment variables resolved via `IConfiguration`.

---

## File system and temp files

Agent tasks that require temporary file writes **must not use `/tmp/`** — this triggers a permissions prompt in this environment.

**Use the workspace-local temp directory instead:**

```bash
# In shell steps
mkdir -p "$GITHUB_WORKSPACE/.tmp"
export TMPDIR="$GITHUB_WORKSPACE/.tmp"
```

```yaml
# In workflow env blocks
env:
  TMPDIR: ${{ github.workspace }}/.tmp
  TMP: ${{ github.workspace }}/.tmp
  TEMP: ${{ github.workspace }}/.tmp
```

The `.tmp/` directory is `.gitignore`d. All scratch writes go here. If a tool hardcodes `/tmp` and does not honour `TMPDIR`, add a comment flagging it for review — do not silently work around it.

---

## What NOT to do

- **Do not** add NuGet packages without first checking if the functionality exists in the BCL or an existing dependency.
- **Do not** use `dynamic` or untyped `object` where a typed abstraction is achievable.
- **Do not** write synchronous file or network I/O in async code paths.
- **Do not** commit secrets, API keys, or connection strings — use environment variables or a secrets manager.
- **Do not** use `Console.WriteLine` — use `ILogger`.
- **Do not** use `.Result`, `.Wait()`, or blocking async calls — use `await`.
- **Do not** write temp files to `/tmp/` — use `$GITHUB_WORKSPACE/.tmp` (see above).
- **Do not** catch `Exception` at a broad scope and continue silently.
- **Do not** implement `NotImplementedException` stubs on interface members and commit them — either implement or redesign the interface.
- **Do not** skip XML doc comments on public API surface.
- **Do not** use Moq — use NSubstitute.

---

## Pull request checklist

Before marking a PR ready for review, verify:

- [ ] `dotnet build` passes with zero warnings
- [ ] `dotnet test --filter "Category!=Integration"` passes
- [ ] SOLID principles applied — no obvious SRP, OCP, or DIP violations
- [ ] All public types and members have XML doc comments
- [ ] No plaintext secrets in any file
- [ ] No new `// TODO` without an associated issue reference
- [ ] `CancellationToken` threaded through all new async call chains
- [ ] `TMPDIR` / temp path rules followed in any shell or workflow steps added