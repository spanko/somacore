# Code conventions

This document is opinionated by design. Consistency beats individual preference.

## Languages and frameworks

- **C# 13 / .NET 9.** Nullable reference types enabled. Implicit usings enabled.
- **ASP.NET Core minimal APIs** for the HTTP surface. No MVC controllers in this codebase.
- **EF Core 9** for data access. Drop to Dapper or raw `NpgsqlCommand` only when the generated SQL is provably wrong or slow.
- **Serilog** for logging, with the App Insights sink in production.
- **xUnit** for tests. **FluentAssertions** for readability. **NSubstitute** for mocks (preferred over Moq).

## Project layout

```
src/
├── SomaCore.Api/                # HTTP surface, DI composition root
├── SomaCore.Domain/             # Domain types, value objects, no I/O dependencies
├── SomaCore.Infrastructure/     # Postgres, Key Vault, WHOOP HTTP client
├── SomaCore.IngestionJobs/      # Container Apps Jobs entry point
tests/
├── SomaCore.UnitTests/
└── SomaCore.IntegrationTests/   # Testcontainers Postgres
```

`Domain` depends on nothing. `Infrastructure` depends on `Domain`. `Api` and `IngestionJobs` depend on `Infrastructure` and `Domain`. **Never** the reverse.

## Naming

- **PascalCase** for types and methods. **camelCase** for parameters and locals. **_camelCase** for private fields.
- **Async methods end in `Async`** unless the type itself signals async (e.g., `IAsyncEnumerable<T>` returns).
- **Interfaces start with `I`**. (Yes, the .NET community is divided. We pick the conventional side.)
- **Test methods** use the `Should_*_when_*` pattern: `Should_reject_webhook_when_signature_is_invalid`.

## Async

1. **Async all the way.** Every I/O method is `async Task<T>`. No sync-over-async.
2. **Cancellation tokens propagate.** Every async method takes a `CancellationToken` parameter and passes it down.
3. **`ConfigureAwait(false)` is not used in app code** — ASP.NET Core has no SyncContext, so it's noise. Use it only in library code.

## Errors

1. **Throw exceptions for unexpected failures.** Database is unreachable, external API returned a malformed response, configuration is missing.
2. **Return `Result<T>` (or `OneOf<T, TError>`) for expected failures.** Token refresh failed because WHOOP returned 401. Webhook signature didn't validate. The user has not connected WHOOP.
3. **Custom exceptions extend `SomaCoreException`** (a simple base class we'll define). Never throw raw `Exception` or `ApplicationException`.
4. **Catch narrowly.** Catch the specific type that can be thrown; never catch `Exception` except at the absolute outer boundary (Program.cs, the top of an async background job).

## Logging

1. **Structured logging only.** Properties go in placeholders, never in the format string:

   ```csharp
   // GOOD
   _logger.LogInformation(
       "Webhook received {EventType} for {WhoopUserId} with trace {TraceId}",
       eventType, whoopUserId, traceId);

   // BAD — never do this
   _logger.LogInformation($"Webhook received {eventType} for {whoopUserId}");
   ```

2. **Never log secrets or tokens or full webhook payloads.** Trace IDs and event IDs are fine.
3. **Log levels:**
   - `Trace`: extremely verbose, off in dev by default
   - `Debug`: dev troubleshooting
   - `Information`: normal operational events (webhook received, plan generated, user signed in)
   - `Warning`: recoverable problems (token refresh retried successfully)
   - `Error`: an operation failed and the system handled it
   - `Critical`: system is in a degraded state and needs human attention

## Configuration and secrets

1. **Strongly-typed options.** Bind `IConfiguration` to record types under `SomaCore.Api.Configuration` (or the equivalent project namespace). Validate at startup with `ValidateDataAnnotations().ValidateOnStart()`.
2. **Secrets via Key Vault references.** In Container Apps, set env vars to `@Microsoft.KeyVault(SecretUri=...)` references. Never paste secrets directly in the env-var values.
3. **Local dev uses dotnet user-secrets.** Initialized via `dotnet user-secrets init` per-project; values populated from the dev Key Vault.
4. **Never commit `appsettings.Development.json` with real values.** Use `appsettings.Development.json.example` as a template that is committed; `appsettings.Development.json` itself is gitignored.

## Database

1. **Migrations are the source of truth.** Schema changes always go through `dotnet ef migrations add`. Hand-edited SQL on prod is forbidden.
2. **Prefer `IQueryable` for reads via EF Core.** Use `.AsNoTracking()` for read-only queries — every read-only query.
3. **Use Dapper (or raw Npgsql) for:**
   - Reports and aggregations EF Core compiles inefficiently
   - Bulk inserts (use Npgsql's COPY)
   - Anything where the generated SQL surprises you on `EXPLAIN`
4. **Database transactions are explicit.** Use `await using var tx = await dbContext.Database.BeginTransactionAsync(ct);` and call `CommitAsync()` / `RollbackAsync()` deliberately.

## API surface

1. **Minimal API endpoints are grouped** by feature in static methods that take `RouteGroupBuilder`. One file per feature group.

   ```csharp
   public static class WhoopAuthEndpoints
   {
       public static void Map(RouteGroupBuilder group)
       {
           group.MapGet("/start", StartAsync);
           group.MapGet("/callback", CallbackAsync);
       }
       // handlers...
   }
   ```

2. **Use `Results.Ok(...)` / `Results.NotFound(...)` etc.** Never return raw values from minimal API handlers.
3. **DTOs are records.** `public record RecoverySnapshot(int Score, double HrvMs, double RhrBpm, string ScoreState);`
4. **Validation via `Microsoft.AspNetCore.Http.Validation`** (the .NET 9 endpoint validation) or FluentValidation if it gets complex.

## Testing

1. **Unit tests cover pure logic.** No DB, no network, no clock dependency that isn't injected.
2. **Integration tests use Testcontainers** for Postgres. They are slower but real.
3. **The webhook handler has both:** a unit test for HMAC validation logic, an integration test for the full flow.
4. **Don't mock what you don't own** unless you must. Prefer testing real implementations behind a port.
5. **Test names describe behavior**, not method names. `Should_reject_webhook_when_signature_is_invalid` not `TestVerifySignature1`.

## Git and PRs

1. **Branch names:** `feat/<short-desc>`, `fix/<short-desc>`, `chore/<short-desc>`, `docs/<short-desc>`.
2. **Commit messages:** imperative mood, present tense. "Add HMAC validation to webhook handler" not "Added" or "Adding".
3. **PRs link to the ADR(s)** they implement, if any.
4. **PRs run `dotnet format` clean.** CI rejects unformatted code.
5. **PRs include tests** for non-trivial logic. Reviewer can ask for tests if missing.

## Dependencies

1. **A new NuGet package is a decision.** Add it via an ADR or as a small note in the PR description with reasoning.
2. **Prefer Microsoft and well-known packages** (`Microsoft.*`, `Azure.*`, `Serilog.*`, `Npgsql.*`). Niche packages need justification.
3. **Pin major versions** in `Directory.Packages.props` (Central Package Management). No floating versions.
