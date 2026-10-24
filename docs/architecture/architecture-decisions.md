# Architecture decisions — RestApiCore

Accepted 23/10/2026 with the refactor described in `refactoring-plan.md`. Each decision states the rule a future change must follow.

## ADR-1 Layers and dependency direction

A pragmatic modular monolith on the existing projects. No project was added.

```
            ┌──────────── Api (presentation + composition root) ────────────┐
            │ endpoints, middleware, controllers, JSON contracts, Program.cs │
            └───┬──────────────────────┬────────────────────────────────────┘
                │                      │
                ▼                      ▼
   Business (application)  ◄──── DataAccess (infrastructure: SQL)
   use cases, rules, ports         repositories, FormExecutor, log sink
        │        │                     │            │
        ▼        ▼                     ▼            ▼
     Security   Models              Provider     Utilities
   (crypto, JWT) (DTOs, config,    (SqlDatabase,  (legacy XML/JSON helpers)
                  form metadata)    SqlClient)
```

| Layer | Owns | Must not |
|---|---|---|
| `Api` | HTTP routes, middleware, controllers, request/response JSON, `Program.cs` (composition root), hosted services (`FormReloader`, `LogWorker`) | Contain business rules or SQL. Endpoints read HTTP, call one use case, render the outcome |
| `Business` | Use cases (`FormRequestService`, `PortalLoginService`), security protocol (`MessageAuthenticator`, `AccessTokenIssuer`), stores with caching (`SignerKeyStore`, `UnitRightsChecker`, …), form metadata (`ImportFormLoader/Catalog`, `JsonBodyMapper`, `DataSetPager`), **persistence ports** (`Persistence.cs`, `IFormExecutor`, `IIdempotencyStore`, `IRequestLogSink`) | Reference `DataAccess`, `Provider` or `Microsoft.Data.SqlClient` — **enforced**: `Business.csproj` has no such reference, so it does not compile |
| `DataAccess` | Implementations of the ports: SQL text, parameters, temp tables, transactions, bulk copy, `restApi*` procs | Make business decisions (paging, authorization, error mapping) |
| `Provider` | `SqlDatabase`: connections, commands, readers → `DataSet`, bulk copy, timeout, wrapping `SqlException` in `DatabaseException` | Know any table or proc |
| `Security` | Pure crypto: HMAC canonical forms (`MessageSignature`), JWT (`JwtTokenService`), AES (`EncryptionHelper`, `PrivateKeyProtector`), RSA (`PortalKeyProvider`) | Depend on ASP.NET or SQL |
| `Models` | Configuration (`AppConfig`), immutable form metadata (`ImportForm`), messages, legacy DTOs | Behavior beyond trivial computed properties |

There is no separate Domain project: the ERP's invariants live in customer SQL (`checking`/`processing` queries, FBO procs). Creating entity classes for data that is only carried to SQL would add mapping without adding rules.

## ADR-2 Interfaces versus concrete classes

Use an interface when it is a **port across the Business → DataAccess boundary** or a **seam that tests substitute** (`IFormExecutor`, `IIdempotencyStore`, `IUnitRightsChecker`, `ISignerKeyStore`, `IRevokedTokenStore`, `IIpAccessManager`, `IPortalCredentialStore`, `IRequestLogSink`, the ports in `Persistence.cs`). Ports are narrow and named after what the caller needs (`IUnitRightsRepository.CheckAsync`), not generic repositories.

Use a concrete class when there is one implementation and no boundary to cross: `SqlDatabase` (infrastructure primitive used only by `DataAccess`), `MessageAuthenticator`, `AccessTokenIssuer`, `FormRequestService`, `FormResponseWriter`, `JwtTokenService`. They are still injected, never created with `new` inside other services. A repository consumed only by `Api` (`LogRepository`, `ColumnSchemaRepository`) gets no interface: Api is the composition root and may depend on DataAccess directly.

## ADR-3 Inheritance

Allowed only when the subtype fully honours the base contract. Current uses: `BackgroundService` (`LogWorker`, `FormReloader`), `AuthenticationHandler` (`CustomJwtAuthenticationHandler`), `TimeProvider` fakes in tests, `OptionsRepository : Options` (loader that fills the options object; kept as is). `FormMethodBase`/`FormMethodCache` implement a strategy interface (`IFormMethod`), not a base class, despite the name. No `BaseService`/`BaseRepository`. Prefer composition: e.g. `UnitRightsChecker` (cache + rule) composes `IUnitRightsRepository` (SQL) instead of subclassing it.

## ADR-4 Dynamic form results stay `DataSet`, behind an adapter

The ERP form engine's contract is "customer SQL returns N result sets, table 0 carries `code`/`message`/`tables`". The number of tables and their columns are unknown at compile time, and `SqlBulkCopy` consumes `DataTable`. Therefore:

- `DataSet`/`DataTable` remain the representation for **dynamic form data only**: `MappedBody.Data` (bulk source), `FormExecutionResult.Data`, `DataSetPager`, `FormResultSet.Tables`, legacy pipeline.
- `FormResultSet` (Business) is the single place that interprets table 0 (`Code`, `Message`, table naming `tables` → `data`/`dataN`). Nothing else reads `Tables[0]["code"]`.
- Use cases return typed `FormOutcome` records (`FormFailed`, `FormInvalid`, `FormDryRun`, `FormCompleted`, `FormPaged`); `FormResponseWriter` (Api) is the only code that turns them into JSON. Fixed-shape data (users, rights, idempotency rows, revoked tokens) is mapped to typed models inside the repository and never leaves it as `DataSet`.
- SQL text, parameters, `SET XACT_ABORT ON`, T-SQL transactions on one connection, and rollback on cancellation are unchanged.

## ADR-5 Security responsibilities

| Concern | Owner |
|---|---|
| Which `/api` routes must be signed; signer resolution (token body / JWT claims + revocation); portal-scope policy; lockout accounting; body size limit; response buffering | `SignatureMiddleware` (HTTP coordination) |
| Nonce format + clock skew, HMAC verification (constant-time), replay store (nonce written **after** a valid signature, kept `2 × clockSkewSeconds` in-process `IMemoryCache`), response canonicalisation + signing | `MessageAuthenticator` (Business) over `MessageSignature` (Security) |
| Key lookup and private-key decryption | `SignerKeyStore` + `PrivateKeyProtector` |
| JWT creation | `AccessTokenIssuer` (claims, expiry, random `jti`) over `JwtTokenService` |
| JWT validation (authentication only) | `CustomJwtAuthenticationHandler` |
| Portal password login | `PortalLoginService` |
| Form permission, unit permission | `FormRequestService.AuthorizeAsync` (+ `UnitRightsChecker`) |
| IP allow/deny | `IpAccessMiddleware`, `IIpAccessManager` |

The signing format, canonical strings, claim names, error codes and the order of checks are unchanged; they are pinned by `MessageSignatureTests` (test vectors), `SignatureMiddlewareTests`, `PortalTests` and `MessageAuthenticatorTests`. A valid JWT or signature is not authorization: form and unit rights are always checked by the use case.

## ADR-6 Legacy v2 pipeline: isolate, then remove

`POST /api/auth/{userName}` and `POST /api/query/{name}` are kept because removing a public route needs evidence that no partner calls it, which the repository cannot give. They are isolated:

- collaborators live in `Api/Legacy` (namespace `Web.Legacy`) plus four clearly marked DataAccess classes (`FormRepository`, `CacheRepository`, `ServerRepository.CheckFormRights`, `UserRepository.GetUser`);
- everything is constructor-injected and registered in one block of `ServiceRegistration.AddFastApi`;
- `Business` contains no legacy code.

Deprecation plan: (1) check production request logs (`restApiLogReq.path LIKE '/api/query/%' OR '/api/auth/%'` excluding `token`/`revoke`/`portal`) over a full business cycle; (2) announce the removal date to partners; (3) delete `Api/Legacy`, the two controllers, the four DataAccess members, `Models/Form`, `Models/Request`, legacy `Models/Response/*`, `Update*` models, `CustomJsonConverter`, `RequestHelper`, `ResponseHelper.ToActionResult`, `GenerateAuthKey`/`ComputeMD5Hash`/`GenerateJWTToken`, the legacy `AppConfig` keys, `IsLegacyController`, and accept only v3 tokens in `JwtTokenService.ValidateJWTToken`. Until then the legacy SQL-injection defect (`KE_HOACH` 1.4 #2) remains reachable by holders of a legacy JWT.

## ADR-7 Legacy removal policy

Delete code only when (a) a repository-wide search including configuration, XML, scripts and reflection finds no caller, (b) it is not an HTTP route or file format consumed outside the repository, and (c) the build and tests pass immediately afterwards. Otherwise classify it `LEGACY` or `UNKNOWN` in `obsolete-code-audit.md` with the missing evidence. No temporary duplicates are left after a migration.

## ADR-8 Idempotency belongs to the HTTP adapter

`Idempotency-Key` replays the exact HTTP status and body (and the response is then re-signed for the new request). It is therefore handled in `FormEndpoint` around the use case, with storage behind `IIdempotencyStore`. The fingerprint (form + sorted raw query + SHA-256 of raw body), reservation under `UPDLOCK, HOLDLOCK`, release on unexpected exceptions, and "no key for dryRun" rules are unchanged. Because stored bodies are replayed verbatim, `FormResponseWriter` output must stay byte-stable.
