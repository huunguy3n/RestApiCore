# Obsolete-code audit

Date: 23/10/2026. Scope: every C# type in `Api`, `Business`, `DataAccess`, `Provider`, `Security`, `Models`, `Utilities`, `Cli`, `CallbackWorker`.

## Method

For every candidate the whole repository was searched (excluding `bin/`, `obj/`, `wwwroot/lib/`) in `*.cs, *.cshtml, *.json, *.xml, *.csproj, *.js, *.md, *.sql, *.config, *.ps1` for:

- type name and member names (construction, injection, generic arguments, attributes);
- DI registrations in `Api/Custom/ServiceRegistration.cs` (called by `Program.cs`) and in the TestServer hosts in `Tests/*`;
- runtime discovery: `Type.GetType`, `Activator`, `Assembly.Load/GetTypes`, `AddApplicationPart`, `typeof(..).Assembly` — **no hits**. The only runtime discovery in the solution is MVC controller scanning (`AddControllers` + `MapControllers`);
- configuration and XML files that could name a type (`appsettings*.json`, `App_Data/**/*.xml`) — no type names are loaded from configuration;
- build and deployment scripts — the repository has none (`*.ps1`, `*.cmd`, `*.pubxml`, `web.config`, `Directory.Build.*`: no files).

External consumers: every project is an internal assembly deployed together with `Api` (or `Cli`); none is packaged or published as a library, so "public but unreferenced" is not treated as an external contract. HTTP routes **are** an external contract and are never removed on reference counts alone.

Classifications: `ACTIVE`, `DUPLICATED`, `LEGACY`, `DEAD`, `UNKNOWN`.

## Removed (DEAD)

| Class / member | Classification | Evidence | References searched | Replacement | Decision | Remaining uncertainty |
|---|---|---|---|---|---|---|
| `Utilities/Logger.cs` (`Utilities.Logger`) | DEAD | Empty class; the 43 "Logger" hits are all `ILogger<>`/`NullLogger` | Name, `new Logger`, DI | Serilog / `ILogger<T>` | **Removed** | None |
| `Api/Custom/LogActionFilter.cs` | DEAD | Registered as MVC filter but both methods only compute locals; the logging calls are commented out. No side effect | Name, `Filters.Add` | `RequestLogMiddleware` | **Removed** with its registration | None |
| `Business/LogManager.cs` | DEAD (4 of 5 methods) | `WriteLog`, `WriteLogActivity`, `WriteLogTransfer` are empty; `WriteLogDataAsync` has no caller (also noted in `KE_HOACH` §5 "không ai gọi"). The live method `WriteLogErrorAsync` was a one-line forward | Method names, DI | `LogRepository.WriteAsync` | **Removed**; Program's exception handler calls `LogRepository` | None |
| `LogRepository.InsertLogDataAsync`, `WriteLogData`, `RemoveLogData` | DEAD | Only caller was `LogManager.WriteLogDataAsync` (dead) / no caller. `RemoveLogData` calls `restApi_RemoveLogData`, which is in no script under `sql/` | Method names, proc name | `SqlRequestLogSink` + `restApi_LogCleanup` | **Removed** | Table `restApiLogData` stays (no schema change) |
| `UserRepository.GetUser(int)`, `GetUserList`, `AddUser`, `UpdateUser`, `DeleteUser` | DEAD | No callers; the last three are empty | Method names | — | **Removed** | None |
| `Api/Controllers/CPanelController.cs`, `DocumentController.cs` | DEAD | No actions, so MVC maps no endpoint. Their only effect was the HMAC exemption of `/api/cpanel*` and `/api/document*` in `SignatureMiddleware.IsLegacyController` | Names, routes, middleware | — | **Removed**, exemption removed (see "Behavior changes") | None |
| `ResponseHelper.ToContentResult` | DEAD | No caller (comment says "used in ActionFilter"; the filter never called it) | Name | — | **Removed** | None |
| `Models/Interface/INotify.cs`, `Models/Notify.cs` | DEAD | `INotify` is `internal` and empty; `Notify` has no reference | Names | — | **Removed** | `restApiNotify` table untouched |
| `Models/Log/LogActivity.cs`, `LogError.cs` | DEAD | `internal`, empty | Names | — | **Removed** | None |
| `Models/Log/LogData.cs`, `Models/Interface/ILog.cs` | DEAD | `LogData` never constructed; `ILog` only implemented by `LogData` | Names | `RequestLogEntry` | **Removed** | None |
| `Models/Interface/IRequest.cs`, `Models/Request/Request.cs` | DEAD | `IRequest` was only consumed by the dead `LogManager` methods; `Request` never constructed | Names | — | **Removed**; `: IRequest` dropped from `RequestAuth`/`RequestQuery` | None |
| `AppConfig.checkTypeResponse` | DEAD | Declared and defaulted, never read; not present in `appsettings*.json` | Name | — | **Removed** (binder ignores unknown keys, so old config files still load) | None |
| `CacheRepository.RemoveCache`, `fileCachePartern`; `ResponseManager.memoryCachePartern` | DEAD | No callers. Note: `RemoveCache` was the only cleanup of legacy cache files and was never scheduled | Names | — | **Removed** | Legacy `Cache/` files are not cleaned (pre-existing) |
| `Provider.Bulk(SqlConnection, DataSet)`, `Provider.Execute(string, …)` (sync), `Provider.CloseConn` | DEAD | No callers after `using` replaced `CloseConn` | Names | `SqlDatabase` | **Removed** | None |
| `Business/PortalCredentials.cs` (`PortalCredentialStore`) | DUPLICATED → removed | A pass-through: `new UserRepository(appConfig).GetEncryptedPasswordAsync(...)` | Name, DI | `UserRepository` implements `IPortalCredentialStore` directly | **Removed** | None |
| `AuthManager.AuthenticateToken` | DUPLICATED → removed | Built a new `JwtTokenService` per request only to call `ValidateJWTToken` | Name | `CustomJwtAuthenticationHandler` uses the injected `JwtTokenService` | **Removed** | None |

## Kept

| Class / file | Classification | Evidence | Replacement | Decision | Remaining uncertainty |
|---|---|---|---|---|---|
| `Api/Controllers/AuthController.cs` (`POST /api/auth/{userName}`) | LEGACY | Live public route; HMAC-exempt; MD5 `GenerateAuthKey` login; JWT with audience = client IP | `/api/auth/token`, `/api/auth/portal` | Kept, constructor-injected, moved collaborators to `Api/Legacy` | **Whether any partner still calls it** cannot be proven from the repository |
| `Api/Controllers/QueryController.cs` (`POST /api/query/{name}`) | LEGACY | Live public route; **contains the known SQL injection** (`FormRepository.ReplaceDefaultVaraible`, `unit` header) and the shared-form race (`KE_HOACH` 1.4 #2, #4). Only `App_Data/Form/setTemplate.xml` is still in v2 format; the two v3 files throw | `FormEndpoint` | Kept and isolated; see deprecation plan in `architecture-decisions.md` (ADR-6) | Same as above. Recommended: remove (decision pending) |
| `Api/Legacy/*` (`AuthManager`, `FormManager`, `IFormMethod`, `FormMethodBase`, `FormMethodCache`, `LegacyResponseManager`) | LEGACY | Only reachable from the two controllers above. `FormMethodBase`/`FormMethodCache` are a valid two-strategy hierarchy (execute vs. read cached page) behind `IFormMethod` — no duplicated behavior between them | v3 pipeline | Kept, DI-registered, no `new` | Deleted together with the controllers |
| `DataAccess/FormRepository.cs`, `CacheRepository.cs`, `ServerRepository.CheckFormRights`, `UserRepository.GetUser(string)` | LEGACY | Only used by `Api/Legacy` | `ImportFormLoader`, `FormExecutor`, `restApi_GetUserRights` | Kept | Same |
| `Models/Form/*`, `Models/Request/*`, `Models/Response/ResponseAuth.cs`, `ResponseQuery.cs`, `ResponseData.cs`, `Models/Options/Update*.cs`, `Options.Updates`, `OptionsRepository.GetUpdateList` | LEGACY | Only used by the legacy pipeline | `Models/ImportForm.cs` | Kept | Same |
| `Utilities/CustomJsonConverter.cs`, `Api/Custom/RequestHelper.cs`, `ResponseHelper.ToActionResult` | LEGACY | Only used by the legacy pipeline | `JsonBodyMapper`, `SignatureMiddleware` body reading | Kept | Same |
| `Security/EncryptionHelper.GenerateAuthKey`, `ComputeMD5Hash`; `JwtTokenService.GenerateJWTToken`; `AppConfig.positions/maxRowInResponse/cacheDurationInMinutes/expiryToken` | LEGACY | Legacy login/paging; `GenerateJWTToken` is also used by a v3 test proving old tokens are rejected | — | Kept | Same |
| `App_Data/Options/Update.xml` | LEGACY / ACTIVE for CLI | Read by `OptionsRepository` (legacy) **and** by `Fast.Api.Cli upgrade` (default path) | — | Kept | None |
| `App_Data/Form/setTemplate.xml` | LEGACY | v2 `<form>` file; the v3 catalog logs it as an error on every load | Upgrade with `Fast.Api.Cli upgrade` | Kept | Remove or upgrade together with the v2 pipeline |
| `UserRights.IsIpAllowed`, `MatchIp`, `AllowIps`, `BlackListIps` | DUPLICATED | C# copy of the IP rule; production uses `restApi_CheckIPAddress`. Only `UserRightsTests` call it | `IpAccessManager` → `restApi_CheckIPAddress` | Kept | Decide which implementation is authoritative before deleting either (security semantics) |
| `SignerKeyStore.InvalidateAsync`, `IpAccessManager.InvalidateAsync` | UNKNOWN | No caller; documented hooks for "make key/IP change effective immediately". The CLI runs in another process and cannot call them | — | Kept | Needs an admin endpoint or a decision that the cache TTL is enough |
| `CallbackWorker` project | LEGACY (placeholder) | Template worker; `KE_HOACH` 1.1 lists callback/notify as out of scope for this phase | — | Kept | Planned component |
| `ApiDocs/wwwroot/js/site.js`, `Pages/Shared/_ValidationScriptsPartial.cshtml`, `wwwroot/lib/*` | UNKNOWN (front-end) | Razor template leftovers, not referenced by `Index.cshtml`/`_Layout.cshtml` | — | Not in scope of this class audit | Needs a portal asset review |

## Behavior changes caused by removal

| Change | Before | After | Why |
|---|---|---|---|
| `POST /api/cpanel*`, `/api/document*` | HMAC-exempt, fell through to `FormEndpoint` → `404 NOT_FOUND` (or `NullReferenceException` if a form with that resource existed) | Signature required → `401 UNAUTHORIZED` without a valid signed request | No endpoint ever existed; the exemption was an unsigned path into the form engine |
| `FormEndpoint` without a signer | `NullReferenceException` → 500 | `401 UNAUTHORIZED` (after route resolution, so unknown routes still return 404/405) | Defensive guard for a mis-wired pipeline |

Both are covered by tests (`SignatureMiddlewareTests.TienToControllerRongDaBo_PhaiKy`, `FormEndpointTests.ThieuSigner_Unauthorized_KhongChayForm_RouteKhongCoVan404`).
