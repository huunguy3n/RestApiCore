# Architecture assessment — RestApiCore (FastAPI v3)

Snapshot: 23/10/2026, before the refactor, on top of the uncommitted working tree (23 modified files) at `master` (`d148afd`).

## 1. Baseline

| Item | Value |
|---|---|
| Target framework | `net10.0` on every project, `Nullable` + `ImplicitUsings` enabled, default C# version (14) |
| SDK | .NET SDK 10.0.401 |
| Hosting | ASP.NET Core on IIS (Windows), SQL Server 2008 R2 minimum |
| Packages | `Microsoft.Data.SqlClient` 6.1.7, `System.IdentityModel.Tokens.Jwt` 8.23.0, `Microsoft.Extensions.Caching.Hybrid` 10.11.0, `Serilog.AspNetCore` 10.0.0, `Newtonsoft.Json` 13.0.4, xUnit 2.9.3, `Microsoft.AspNetCore.TestHost` 10.0.13 |
| Build | `dotnet build RestApiCore.sln`: succeeded, 0 errors. Clean rebuild of `HEAD`: **59 warnings**, almost all nullable warnings (CS86xx) in the legacy v2 code (an incremental build had shown 0) |
| Tests | `dotnet test Tests/Tests.csproj`: **279 passed, 0 failed, 0 skipped** |
| Pre-existing failures | None |
| Not runnable here | Anything that needs SQL Server (repositories, `FormExecutor`, idempotency SQL, log sink, health check `sql`). There is no SQL integration test project |

## 2. Projects and dependency direction (before)

```
Api ──► Business ──► DataAccess ──► Provider (static ADO.NET, SqlClient)
 │          │            │  └──────► Utilities ──► Models
 │          │            └─────────► Models
 │          ├──► Security (JWT, HMAC, AES, RSA)
 │          └──► Models
 ├──► DataAccess
 └──► Models
Cli ──► Provider, Security, Business          CallbackWorker (template, no references)
WebPortal (Razor portal, no references)         Tests ──► Api, Business, Security, Utilities, Models
```

The direction is a classic "layered" chain where the **application layer (Business) depends on the data layer (DataAccess) and on SQL directly**. Nothing stops SQL from appearing in Business, and it does.

### Two request pipelines share the host

| Pipeline | Entry | Flow | State |
|---|---|---|---|
| v3 (current) | `MapForms` → `FormEndpoint` (`/api/{**path}`), `MapAuth`, `MapPortal`, `_meta/*` | `SignatureMiddleware` → `FormEndpoint` → `JsonBodyMapper` → `IFormExecutor`(`FormExecutor`) | Active, covered by tests |
| v2 legacy (pre-v3 RestApiCore engine) | `AuthController` (`POST /api/auth/{userName}`), `QueryController` (`POST /api/query/{name}`) | `RequestHelper` → `FormManager` → `FormMethodBase`/`FormMethodCache` → `FormRepository`, `ResponseManager` (paging), `CacheRepository` | **Still routable**, exempted from HMAC by `SignatureMiddleware.IsLegacyController`, no tests |

## 3. Confirmed design problems

### 3.1 Static infrastructure and global mutable state
- `Provider/Provider.cs:6` — `public static class Provider` with `public static int CommandTimeout { get; private set; }` (line 12) mutated once by `Program.cs:46` (`Provider.Provider.Configure`). Every repository and several Business classes call it statically, so no class declares its SQL dependency and none can be substituted.
- `Provider.cs:148-274` — sync legacy API: `OpenConn` returns `null` on empty connection string; `Execute(string…)`/`Retrieve(string…)` open a connection and close it **without `using`/`finally`**, so an exception leaks the connection; `Bulk` never disposes `SqlBulkCopy`; `Bulk(SqlConnection, DataSet)` has no caller.
- `Provider.cs:54,105,126,192,217,238` — every `SqlException` is rewrapped as plain `System.Exception`, so callers cannot tell a database failure from any other failure.
- `DataAccess/FormRepository.cs:16` — `private static readonly ConcurrentDictionary<string, Form> _forms`: process-wide cache that request code then mutates (see 3.6).
- `Api/Controllers/AuthController.cs:16` — static `ConcurrentDictionary` for legacy login replay protection.

### 3.2 Application code depends on concrete infrastructure (DIP)
Direct `new Repository(...)` or `Provider.Provider` calls inside Business:

| Location | Concrete dependency |
|---|---|
| `Business/SignerKeyStore.cs:59` | `new UserRepository(appConfig)` |
| `Business/TokenStores.cs:24,32` | `new RevokedTokenRepository(appConfig)` (twice) |
| `Business/IpAccessManager.cs:25` | `new ServerRepository(appConfig)` |
| `Business/PortalCredentials.cs:15` | `new UserRepository(appConfig)` (a pass-through class with no logic) |
| `Business/LogManager.cs:48,55` | `new LogRepository(appConfig)` |
| `Business/UnitRightsChecker.cs:32` | `Provider.Provider.RetrieveAsync` + `SqlParameter` |
| `Business/IdempotencyStore.cs:36,77,91` | Raw T-SQL + `Provider.Provider` — this is a repository living in Business |
| `Business/FormExecutor.cs:46-147` | `SqlConnection`, temp tables, T-SQL transactions — an execution gateway living in Business |
| `Business/Logging/RequestLog.cs:99-254` | `SqlRequestLogSink`: `SqlConnection`, `SqlBulkCopy`, `SqlTransaction` |
| `Business/ResponseManager.cs:156,240` | `new CacheRepository(appConfig)` (legacy paging) |
| `Business/FormManager.cs:18,25,29`, `FormMethod/FormMethodBase.cs:21`, `AuthManager.cs:19,29,45` | `new FormRepository`, `new FormMethodCache/Base`, `new UserRepository`, `new JwtTokenService` |
| `Api/Custom/FormRuntime.cs:32` | `new ColumnSchemaRepository(appConfig)` inside `FormLoader` |
| `Api/Custom/HealthChecks.cs:18` | Builds its own `SqlConnection` from `AppConfig` |

### 3.3 Duplicated security construction
`new JwtTokenService(appConfig.secretKey, appConfig.expiryToken, appConfig.issuerToken)` is built per call in `Api/Custom/AuthEndpoints.cs:39`, `Api/Custom/PortalEndpoints.cs:106`, `Business/AuthManager.cs:29` and `:45` (the last one runs on **every authenticated request** through `CustomJwtAuthenticationHandler.cs:30`). Token issuance (expiry from `TimeProvider`, random `jti`, claims) is duplicated between `AuthEndpoints.IssueTokenAsync` and `PortalEndpoints.LoginAsync`.

### 3.4 High-responsibility classes (SRP)

| Class | Responsibilities mixed today |
|---|---|
| `Api/Custom/FormEndpoint.cs` (350 lines, static) | HTTP writing; route resolution; form permission; unit rights; cursor paging rules (owner/form/unit check, page range); Transfer marking before data is returned; idempotency fingerprint + reservation; dry-run; interpretation of the processing `DataSet` (table 0 = code/message/tables, naming `data`/`dataN`); paging vs `maxRowsWithoutPaging` rule; JSON contract building; status mapping from `Message.xml`. Resolves 9 services through `RequestServices.GetRequiredService` (service locator) |
| `Api/Custom/SignatureMiddleware.cs` (276 lines) | Route policy (which paths are signed), signer resolution (token body / JWT claims / revocation), portal-scope policy, lockout accounting, body reading with limit, nonce format + clock skew, HMAC verification, replay (nonce) store, response buffering, response canonicalisation + signing |
| `Api/Custom/PortalEndpoints.cs` `LoginAsync` | Full portal authentication workflow (account, lockout, IP, `kid`, RSA decrypt, timestamp, nonce, AES password decrypt + constant-time compare) plus JWT issuance, inside an endpoint |
| `Business/ResponseManager.cs` | Message → response mapping (used everywhere) **and** legacy paging/caching that writes JSON files and `restApiCache` rows |
| `Business/AuthManager.cs` | Legacy MD5 login (`AuthenticateUser`) **and** JWT validation for every request (`AuthenticateToken`) |
| `Business/LogManager.cs` | One live method (`WriteLogErrorAsync`) and four empty/unused ones |

### 3.5 Data model and persistence boundaries
- `System.Data.DataSet` is the native contract of the ERP form engine: `processing` returns N result sets whose shape is defined by customer SQL, table 0 carrying `code`/`message`/`tables`. Interpretation of that contract is inlined in `FormEndpoint.DataSetResponseAsync` (lines 231-292) and `FormExecutor.ResultCode`; there is no typed model for "form result".
- `MappedBody.Data` (`DataSet`) is the bulk-copy source for temp tables — a deliberate, appropriate use.
- Legacy models (`Models/Form/*`, `Models/Request/*`, `ResponseAuth/Query/Data`) are mutable, shared across requests and mixed with v3 models (`Models/ImportForm.cs`, immutable records).
- `UserRights` (`Models/UserRights.cs:42-59`) re-implements IP allow/deny matching in C#, while production uses `restApi_CheckIPAddress` (`ServerRepository.CheckIPAddressAsync`). The C# copy is only exercised by tests.

### 3.6 Security and correctness findings (legacy pipeline)
- **SQL injection**: `DataAccess/FormRepository.cs:274-286` (`ReplaceDefaultVaraible`) pastes the `unit` header and `userName` into SQL literals without escaping. Reachable via `POST /api/query/{name}` with a legacy JWT. This is `KE_HOACH` 1.4 #2, fixed only in v3.
- **Shared-state race**: `FormMethodBase.ExecuteForm` (lines 24-46) and `FormRepository.GetScriptUpdate/GetScriptCheck` (lines 291-341) mutate the cached `Form` (field types, `Queries[Checking]`) per request (`KE_HOACH` 1.4 #4).
- The v2 engine can only read `<form xmlns="urn:schemas-fast-com:rest-api">` files. Of the three files in `Api/App_Data/Form`, only `setTemplate.xml` is v2; `getCustomer.xml` and `setSaleInvoice.xml` throw in `FormRepository.GetForm` (`Enum.Parse("")`). `setTemplate.xml` in turn is rejected by the v3 catalog on every load.
- `SignatureMiddleware.IsLegacyController` (line 29) exempts `/api/cpanel` and `/api/document` from HMAC although both controllers are empty (no endpoints). Requests there fall through to `FormEndpoint`; any future form whose resource is `document`/`cpanel` would run without a signer and fail with `NullReferenceException` at `signer.CanAccessForm`.
- `RequestHelper.GetRequest` (`Api/Custom/RequestHelper.cs:63`) blocks on `ReadToEndAsync().Result`.

### 3.7 Async consistency and cancellation
v3 code is async end-to-end with `CancellationToken`. Exceptions: `FormLoader.Load` blocks on `GetColumnsAsync(...).GetAwaiter().GetResult()` (startup/reload thread, acceptable but undocumented); the legacy pipeline is fully synchronous.

## 4. Risks and priorities

| Priority | Item | Risk of change |
|---|---|---|
| P1 | Invert Business → DataAccess so Business cannot reference SQL (compile-time boundary) | Medium: many files move; covered by unit + TestServer tests |
| P1 | Replace static `Provider` with an injected instance (`SqlDatabase`) | Low: mechanical; SQL text and parameters unchanged |
| P1 | Split `FormEndpoint` into HTTP adapter + application use case + typed result adapter | Medium: response JSON must stay byte-identical (idempotency replays stored bodies) |
| P1 | Extract signature verification / response signing / portal login / token issuance from HTTP code | Medium: order of checks and error codes must not change |
| P2 | Isolate the legacy v2 pipeline behind DI and one folder, so it can be deleted in one step | Low |
| P2 | Remove dead code proven by search (see `obsolete-code-audit.md`) | Low |
| P3 | Decide the fate of the v2 pipeline (SQL injection) | Product decision: external partners may still call it |

## 5. Result after the refactor (23/10/2026)

| Item | Before | After |
|---|---|---|
| Business → SQL | Business referenced DataAccess, Provider and SqlClient | No reference; ports in `Business/Persistence.cs`, implemented in DataAccess (compile-time boundary) |
| Static infrastructure | `Provider.Provider` + global `CommandTimeout` | `SqlDatabase` singleton, injected |
| `new Repository/JwtTokenService` in services | 18 sites (table 3.2 + `FormRepository` → `ServerRepository`) | 0 (all constructor injection) |
| `FormEndpoint` | 350 lines, 9 service-locator lookups, business rules | HTTP + idempotency only (~130 lines); rules in `FormRequestService`, JSON in `FormResponseWriter`, DataSet interpretation in `FormResultSet` |
| Security workflow in HTTP code | Verification, signing, portal login, token issuance inline | `MessageAuthenticator`, `PortalLoginService`, `AccessTokenIssuer` |
| Legacy v2 | Spread over Business, DataAccess, Api | `Api/Legacy` + 4 marked DataAccess members, one DI block |
| Dead code | — | 14 files and 17 members removed (`obsolete-code-audit.md`) |
| Build warnings (clean rebuild) | 59 | 39, none new |
| Tests | 279 passed | 317 passed (35 new unit tests, 3 new contract tests) |
