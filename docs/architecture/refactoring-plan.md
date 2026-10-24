# Refactoring plan — RestApiCore

Status: phases R1–R5 **done** on 23/10/2026 (uncommitted working tree); R6 **pending a product decision**. Baseline and problems: `architecture-assessment.md`. Rules: `architecture-decisions.md`. Removals: `obsolete-code-audit.md`.

## Phases

| Phase | Goal | Status |
|---|---|---|
| R1 | Replace the static `Provider` with an injected `SqlDatabase`; invert Business → DataAccess so Business cannot see SQL | Done |
| R2 | Isolate the legacy v2 pipeline behind DI in `Api/Legacy` | Done |
| R3 | Extract security workflows from HTTP code (`MessageAuthenticator`, `AccessTokenIssuer`, `PortalLoginService`) | Done |
| R4 | Split `FormEndpoint` into HTTP adapter / use case (`FormRequestService`) / result adapter (`FormResultSet`) / JSON contract (`FormResponseWriter`) | Done |
| R5 | Remove dead code proven by search; add tests | Done |
| R6 | Remove the legacy v2 pipeline (ADR-6) | **Waiting for evidence that no partner uses `/api/query`, `/api/auth/{userName}`** |

## File-by-file changes

### Provider
| File | Change |
|---|---|
| `Provider/Provider.cs` → `Provider/SqlDatabase.cs` | Static class with global mutable `CommandTimeout` → `sealed class SqlDatabase(connectionString, commandTimeout)` registered as singleton. Same async methods (no connection-string parameter). Sync legacy methods now dispose connections, commands, adapters and bulk copies; dead overloads removed. `SqlException` wrapped in `DatabaseException` (still an `Exception`, `InnerException` unchanged) |

### Business (application) — no longer references DataAccess
| File | Change |
|---|---|
| `Business.csproj` | Removed `DataAccess` reference; added explicit `Newtonsoft.Json` 13.0.4 (was transitive) |
| `Persistence.cs` (new) | Ports: `IUserRightsRepository`, `IPortalCredentialStore` (moved), `IRevokedTokenRepository`, `IIpAccessRepository`, `IUnitRightsRepository` (only ports that Business consumes) |
| `SignerKeyStore.cs`, `TokenStores.cs`, `IpAccessManager.cs`, `UnitRightsChecker.cs` | `new XRepository(appConfig)` / `Provider.Provider` → injected port |
| `FormExecutor.cs` → `FormExecution.cs` | Keeps contracts (`IFormExecutor`, `FormCallContext`, `CheckingError`, `FormExecutionResult`) and `FormVariables`; SQL implementation moved to DataAccess |
| `FormResultSet.cs` (new) | Typed reader of the processing `DataSet` (code, message, named tables) |
| `FormRequestService.cs` (new) | Use case: authorize form/unit, map body, execute, paging, Transfer marking, `RESULT_TOO_LARGE`, cursor pages; returns `FormOutcome` records |
| `MessageAuthenticator.cs` (new) | Nonce freshness, HMAC verification, replay store, response signing |
| `AccessTokenIssuer.cs` (new) | JWT issuance for `api` and `portal` scopes |
| `PortalLoginService.cs` (new) | Portal login workflow (moved from `PortalEndpoints`) |
| `IdempotencyStore.cs` → `Idempotency.cs` | Contract only; SQL moved to `DataAccess/IdempotencyRepository.cs` |
| `ResponseManager.cs` | Only message → response mapping; depends on `Options` only. Legacy paging moved to `Api/Legacy/LegacyResponseManager.cs` |
| `Logging/RequestLog.cs` | `SqlRequestLogSink` moved to DataAccess |
| `LogManager.cs`, `PortalCredentials.cs` | Removed (dead / pass-through) |
| `AuthManager.cs`, `FormManager.cs`, `FormMethod/*`, `Interface/IFormMethod.cs` | Moved to `Api/Legacy` |

### DataAccess (infrastructure)
| File | Change |
|---|---|
| `DataAccess.csproj` | References `Business` (implements its ports) |
| `UserRepository.cs` | `SqlDatabase` injected; implements `IUserRightsRepository`, `IPortalCredentialStore`; dead members removed |
| `RevokedTokenRepository.cs`, `ServerRepository.cs`, `ColumnSchemaRepository.cs`, `LogRepository.cs` | `SqlDatabase` injected; the first two implement Business ports; `ColumnSchemaRepository` and `LogRepository` (reduced to `WriteAsync`) are consumed only by Api and have no interface |
| `UnitRightsRepository.cs`, `IdempotencyRepository.cs`, `FormExecutor.cs`, `SqlRequestLogSink.cs` (moved/new) | SQL formerly in Business; SQL text unchanged |
| `FormRepository.cs`, `CacheRepository.cs` | Legacy; injected `SqlDatabase`/`ServerRepository`; form cache is an instance field of the singleton (same lifetime as the former static) |

### Api (presentation + composition)
| File | Change |
|---|---|
| `Program.cs`, `Custom/ServiceRegistration.cs` (new) | Application registrations moved to `AddFastApi` (grouped: SQL infrastructure, security, business, legacy) so tests build the same graph; host concerns stay in `Program.cs`. `JwtTokenService` singleton. `LogRepository` in the exception handler. `LogActionFilter` and `Provider.Configure` removed |
| `Custom/FormEndpoint.cs` | HTTP + idempotency only; services injected with `[FromServices]` instead of `RequestServices.GetRequiredService`; missing signer → 401 |
| `Custom/FormResponseWriter.cs` (new) | JSON contract of form routes (moved verbatim from `FormEndpoint`) |
| `Custom/SignatureMiddleware.cs` | Delegates to `MessageAuthenticator`; `/api/cpanel`, `/api/document` no longer exempt |
| `Custom/AuthEndpoints.cs`, `Custom/PortalEndpoints.cs` | Use `AccessTokenIssuer` / `PortalLoginService` |
| `Custom/CustomJwtAuthenticationHandler.cs` | Injected `JwtTokenService` (was a new instance per request through `AuthManager`) |
| `Custom/FormRuntime.cs` | `FormLoader` gets `ColumnSchemaRepository` injected |
| `Custom/HealthChecks.cs` | `SqlHealthCheck` uses `SqlDatabase` |
| `Controllers/AuthController.cs`, `QueryController.cs` | Constructor-injected legacy collaborators |
| `Controllers/CPanelController.cs`, `DocumentController.cs`, `Custom/LogActionFilter.cs` | Removed |
| `Legacy/*` | Legacy v2 pipeline (see ADR-6) |

### Models, Security, Utilities
| File | Change |
|---|---|
| `Models/Interface/{ILog,INotify,IRequest}.cs`, `Models/Log/*`, `Models/Notify.cs`, `Models/Request/Request.cs`, `AppConfig.checkTypeResponse` | Removed (dead) |
| `Security/JwtTokenService.cs` | `ValidateJWTToken` declares `ClaimsPrincipal?` (it already returned null) |
| `Utilities/Logger.cs` | Removed (empty) |

## Dependencies and prerequisites
- .NET SDK 10.0.x; no new packages (the explicit `Newtonsoft.Json` reference in Business uses the version already in Utilities).
- No database schema, stored procedure or `sql/` script change.
- Configuration keys unchanged (`checkTypeResponse` is ignored if still present).

## Compatibility risks
| Risk | Mitigation |
|---|---|
| JSON of form responses must stay byte-identical (idempotency replays stored bodies) | Rendering code moved verbatim into `FormResponseWriter`; the 33 `FormEndpointTests` (TestServer) assert codes, statuses, JSON fields, paging and idempotent replay |
| Order of signature checks and error codes | `MessageAuthenticator` keeps the two-step order (freshness → body read → HMAC → nonce); `SignatureMiddlewareTests`, `PortalTests` and new unit tests pin it |
| DI graph errors would only show at runtime | `ServiceRegistrationTests` builds `AddFastApi` with `ValidateOnBuild` and checks every endpoint, middleware, controller, auth handler, health check and `LogWorker` dependency is a registered service. Also checked once by starting `Api.dll` in Development against an unreachable SQL server |
| SQL behavior | SQL text, parameters, transactions unchanged; **not executed in this environment** (no SQL Server available to tests) |
| Two intentional behavior changes | Listed in `obsolete-code-audit.md` → "Behavior changes"; both tested |

## Test strategy
- Unit: `MessageAuthenticatorTests`, `AccessTokenIssuerTests`, `FormResultSetTests`, `FormRequestServiceTests`, `UnitRightsCheckerTests`, `SignerKeyStoreTests`, `SqlDatabaseTests` (`Tests/ApplicationServiceTests.cs`).
- Contract (TestServer): existing `FormEndpointTests`, `SignatureMiddlewareTests`, `PortalTests`, `PortalDocsTests`, `RequestLogTests` — re-registered against the new services, assertions unchanged; two new tests for the intentional changes.
- Not covered automatically: repositories and `FormExecutor` against SQL Server. Before deployment run the `KE_HOACH` end-to-end script on the dev DB: `auth/token` → `_meta/ping` → `customer/get` → `sale-invoice` (dryRun, checking error, idempotent replay, 409) → paging with cursor → Transfer marking → `revoke`, plus `/health/ready`.

## Rollback strategy
The refactor is a pure code change (no schema, config or contract change other than the two documented edge cases). Roll back by redeploying the previous build of `Api` (and `Cli` if redeployed). Git: the changes are uncommitted on `master`; once committed as one commit, `git revert` restores the previous structure.
