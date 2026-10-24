# Refactoring self-review

Date: 24/10/2026. Reviewed the full diff against `HEAD` (`d148afd`) plus the uncommitted working tree, with the ten checks below. Every "Verified" claim has the check that produced it.

## Checks executed

| # | Check | How | Result |
|---|---|---|---|
| 1 | Classes with unrelated responsibilities | Read every changed class; file/type outline | 1 fixed (F3), 4 accepted/open (R3–R6) |
| 2 | Application depends on ASP.NET / SQL / concrete infra | `Business.csproj` references only Models + Security; grep of `Business/**/*.cs` for `Microsoft.AspNetCore`, `Microsoft.Data`, `using DataAccess`, `using Provider`, `HttpContext`, `using Web.` | **0 hits**; compile-time enforced |
| 3 | Base classes, artificial interfaces, generic repositories, service locator, static mutable state | grep for `static` fields/collections, `GetRequiredService`, interfaces with one implementation | No base class or generic repository added; 2 artificial interfaces fixed (F1); service locator only in DI factory lambdas and framework callbacks (rate-limit `OnRejected`, exception handler); 1 static mutable collection left (R2, legacy) |
| 4 | Duplicated validation / authorization / mapping / SQL / exception handling | Read | 1 fixed (F2); 3 open (R7, R8, R10) |
| 5 | Deletions without evidence | Re-ran repo-wide search incl. reflection (`Type.GetType`, `Activator`, `Assembly.*`, `AddApplicationPart`), config, XML, scripts | No reflection or string-based type loading; every deletion has evidence in `obsolete-code-audit.md` |
| 6 | Public API / DI / XML / runtime-discovery regressions | New `ServiceRegistrationTests` (`ValidateOnBuild` + every endpoint, middleware, controller, auth handler, health check and `LogWorker` dependency is a registered service); `Message.xml` codes unchanged; HTTP routes unchanged except the two documented in the audit | Passed (F5) |
| 7 | SQL transactions, HMAC/JWT, idempotency, caching, form execution semantics | (a) every SQL-bearing string literal at `HEAD` compared with the current tree; (b) code comparison of moved methods; (c) contract tests | (a) only the literals of the 5 removed dead methods differ; (b) cache keys, TTLs, claim set, check order, rollback paths unchanged; (c) passed. **Not executed against SQL Server** (R11) |
| 8 | Tests changed only to make the suite pass | `git diff HEAD` of every test file, counting removed `Assert`/`[Fact]`/`[Theory]`/`InlineData` lines | My edits to tests are additions only (DI registrations, a `_withoutSigner` flag, new tests). Removed assertion lines in `SignatureMiddlewareTests` (11), `MessageSignatureTests` (18) and `RequestLogTests` (2) **predate this refactor**: they are part of the uncommitted signature-scheme change that was already in the working tree (R12) |
| 9 | New abstractions without a boundary | Reviewed each new type | `IErrorLog`, `IColumnSchemaReader`, `DatabaseException.Number` removed (F1, F4); remaining new types each own one responsibility (ADR-2) |
| 10 | Circular / inappropriate project references | `ProjectReference` graph | Acyclic: `Api → Business, DataAccess, Models`; `DataAccess → Business, Models, Provider, Utilities`; `Business → Models, Security`; `Cli → Provider, Security, Business`; `Utilities → Models`. No inappropriate edge |

Build and tests after the fixes: `dotnet build RestApiCore.sln --no-incremental` → 0 errors, 39 warnings (all pre-existing nullable warnings in legacy/unchanged code; baseline 59). `dotnet test` → **322 passed, 0 failed, 0 skipped**.

## Findings

| ID | Finding | Severity | Evidence | Status |
|---|---|---|---|---|
| F1 | `IErrorLog` and `IColumnSchemaReader` were defined in Business but only consumed by Api, with one implementation each and no test substitute | Low | `Business/Persistence.cs`; consumers `Api/Program.cs`, `Api/Custom/FormRuntime.cs` | **Fixed**: interfaces removed; Api uses `LogRepository` / `ColumnSchemaRepository` directly (ADR-2 updated) |
| F2 | The nonce replay window `2 × clockSkewSeconds` was computed in two places | Low | `MessageAuthenticator.ReplayWindow`, `PortalLoginService.LoginAsync` | **Fixed**: `NonceStore.ReplayWindow(int)` used by both |
| F3 | `NonceStore` lived inside `SignerKeyStore.cs` (unrelated concern) | Low | `Business/SignerKeyStore.cs` | **Fixed**: `Business/NonceStore.cs` |
| F4 | `DatabaseException.Number` unused, with an unchecked cast | Low | `Provider/SqlDatabase.cs` | **Fixed**: removed |
| F5 | The real DI graph had no automated check; endpoint parameters that are not registered services would fail at request time, not at startup | Medium | `Program.cs` registrations; minimal API parameter inference | **Fixed**: registrations moved to `Api/Custom/ServiceRegistration.AddFastApi` (called by `Program.cs`); `Tests/ServiceRegistrationTests.cs` (5 tests) |
| R1 | Legacy v2 SQL injection (`unit` header pasted into SQL) is still reachable with a legacy JWT | High | `DataAccess/FormRepository.cs` `ReplaceDefaultVaraible`; route `POST /api/query/{name}` | **Open** (pre-existing). Owner is checking production logs; then remove per ADR-6 |
| R2 | Process-wide static `ConcurrentDictionary _lastReceivedTimes`, grows by one entry per user name ever seen | Low | `Api/Controllers/AuthController.cs:19` | **Open** (legacy). Removed with the v2 pipeline |
| R3 | `AppConfig` is a single settings object (including `secretKey`, `passwordKey`, connection string) injected into ~20 classes | Medium | `Models/AppConfig.cs`; constructors across Business/DataAccess/Api | **Open** (pre-existing, ISP). Next step: split into typed options per concern (security, form, log, legacy) and inject only what each class reads |
| R4 | `SignatureMiddleware` still coordinates signer resolution, portal-scope policy, lockout and response buffering | Low | `Api/Custom/SignatureMiddleware.cs` | **Accepted**: HTTP coordination only; crypto/replay is in `MessageAuthenticator`. Extract a signer resolver if it grows |
| R5 | `TokenStores.cs` holds two classes (`RevokedTokenStore`, `LoginLockout`) | Low | `Business/TokenStores.cs` | **Accepted** (file layout only) |
| R6 | `OptionsRepository : Options` uses inheritance to load files in its constructor | Low | `DataAccess/OptionsRepository.cs` | **Open** (pre-existing). Next step: a loader that returns an `Options` instance |
| R7 | The portal login body is read without a size limit; the path is exempt from `SignatureMiddleware`, which enforces `maxRequestBodyBytes` everywhere else | Medium | `Api/Custom/PortalEndpoints.cs` `ReadJsonAsync` | **Open** (pre-existing; bounded only by the server's request limit, 30 MB by default). Next step: reuse the bounded read and return `PAYLOAD_TOO_LARGE` — a contract change, so not done in this refactor |
| R8 | Inconsistent wrapping: `SqlDatabase.OpenConnectionAsync` throws raw `SqlException`, commands throw `DatabaseException`. `restApiLogError` stores only the outer message, so SQL error details are only in the Serilog file | Low | `Provider/SqlDatabase.cs`; exception handler in `Api/Program.cs` | **Open** (same as before the refactor). Next step: wrap open failures too and log `InnerException.Message` |
| R9 | `FormRequestService` depends on `ICursorStore` but calls the static `CursorStore.ToJson` | Low | `Business/FormRequestService.cs` | **Accepted**: pure serialization helper defining the page JSON shape shared with the cursor store |
| R10 | `UserRights.IsIpAllowed` duplicates the `restApi_CheckIPAddress` rule and is used only by tests | Low | `Models/UserRights.cs` | **Open** — decide which implementation is authoritative |
| R11 | SQL paths (`UserRepository`, `FormExecutor`, `IdempotencyRepository`, `UnitRightsRepository`, `SqlRequestLogSink`, legacy repositories) were not executed | Medium | No SQL Server available to the test run | **Open**. Run the `KE_HOACH` end-to-end script on the dev DB before deploying |
| R12 | Assertions removed in three test files | Info | `git diff HEAD -- Tests/SignatureMiddlewareTests.cs Tests/MessageSignatureTests.cs Tests/RequestLogTests.cs` | Not from this refactor (pre-existing uncommitted work); review them with that change |
| R13 | `FormEndpoint` moved from the `RequestDelegate` overload of `Map` to a delegate handled by `RequestDelegateFactory` | Low | `Api/Custom/FormEndpoint.cs` `MapForms` | **Verified**: 404/405/`Allow` header, replay and error tests pass; `[FromServices]` parameters covered by F5 |
| R14 | Startup still fails if SQL is unreachable (form schema lookup), and `FormLoader` blocks on async calls | Low | `Api/Custom/FormRuntime.cs` `FormLoader.Load` | **Accepted** (pre-existing, startup/reload thread only) |
