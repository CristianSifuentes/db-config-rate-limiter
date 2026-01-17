# App DB‑Backed Configuration Catalog + Dynamic Rate Limiting (NET 8)

This repo is a **reference implementation** of a **DB‑backed configuration catalog** that:

- Stores configuration in **extensible tables** (Concept + Entries)
- Supports **scopes** (Global / Tenant / Client / User) for overrides
- Serves **hot‑path settings (rate limiting)** from **in‑memory cached, background‑refreshed snapshots** (no SQL per request)
- Uses **Microsoft.ApplicationInsights** for diagnostics (optional; activates when a ConnectionString exists)

It’s designed to match enterprise constraints:

- **Startup.cs / Program.cs needs values immediately** for service registration
- Config must be **changeable without redeploy**
- Architecture must scale beyond rate limiting (WAF, token age, feature flags, etc.)

---

## Project structure

```
/App-db-config-rate-limiter
  /src
    /App.ConfigCatalog.Domain          # contracts + typed option models
    /App.ConfigCatalog.Infrastructure  # EF entities + provider + cache + warmup service
    /App.ConfigCatalog.Api             # sample Web API showing RateLimiter integration
```

---

## Database model (extensible)

### ConfigConcepts
Represents *what* you are configuring.

- `Key` examples: `rate_limits`, `waf`, `token_age`, `features`

### ConfigEntries
Represents *values* for a concept.

- `Key` examples under `rate_limits`: `global`, `enterprise`
- `ScopeType`: `Global`, `Tenant`, `Client`, `User`
- `ScopeKey`: optional id (tenant id, client id, user id)
- `ValueType`: `json`, `int`, `bool`, `string`
- `Value`: scalar or JSON payload

This lets you extend the system without schema changes.

---

## How rate limiting is stored

Concept: `rate_limits`

- Entry `global` (JSON) → `RateLimitOptions`
- Entry `enterprise` (JSON) → `RateLimitingEnterpriseOptions`

Overrides are done by adding another `ConfigEntries` row with:

- same `ConceptId` + same `Key`
- but `ScopeType = Tenant/Client/User` and `ScopeKey = <id>`

Precedence used by the provider:

`User > Client > Tenant > Global`

---

## Runtime strategy (why it’s fast)

Rate limiting is executed on every request, so **do NOT query SQL from the policy**.

This implementation:

- reads config through `DbConfigProvider` (cache + scope precedence)
- uses `RateLimitConfigAccessor` (volatile snapshots)
- keeps everything warm via `RateLimitConfigWarmupHostedService`

Result:

- request path does **in‑memory reads only**
- DB is hit only by the refresh loop or cache misses (short TTL)

---

## Run locally

### Prereqs

- .NET SDK 8

### Start

```bash
cd src/App.ConfigCatalog.Api
dotnet restore
dotnet run
```

The demo uses **SQLite** by default (`App-config.db`) so you can run instantly.

To switch to SQL Server, change `ConnectionStrings:ConfigCatalog` to a SQL Server connection string and update the provider in `Program.cs` accordingly.

---

## Demo endpoints

- `GET /health`
- `GET /whoami` (shows keys used by rate limiting identity selection)
- `GET /admin/config/rate-limits/global`
- `PUT /admin/config/rate-limits/global` (updates JSON for global limiter)
- `GET /admin/config/rate-limits/enterprise`
- `PUT /admin/config/rate-limits/enterprise`

> These admin endpoints are intentionally simple for the demo.
> In production, protect them with your existing Entra ID policies.

---

## How to integrate into your App solution

### 1) Add the tables and entities
Either:

- **Option A (recommended):** add `DbSet<ConfigConcept>` and `DbSet<ConfigEntry>` into your existing `AppDbContext`
- **Option B:** keep a dedicated `AppConfigDbContext` (as in this demo)

### 2) Register services

Use the same registration pattern as the demo:

- `AddDbContextFactory<AppDbContext>()`
- `AddMemoryCache()`
- `AddSingleton<IConfigProvider, DbConfigProvider>()`
- `AddSingleton<IRateLimitConfigAccessor, RateLimitConfigAccessor>()`
- `AddHostedService<RateLimitConfigWarmupHostedService>()`

### 3) Replace appsettings usage inside AddRateLimiter

Anywhere you currently do:

```csharp
var limits = ctx.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
```

Switch to:

```csharp
var limits = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>().Global;
```

And for policy settings:

```csharp
var enterprise = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>()
    .GetEnterpriseForTenant(tenantId);
```

---

## Notes on middleware ordering (security observability)

Put **correlation/trace identifiers early** so they decorate all telemetry/logs:

1. CorrelationId
2. WAF signals
3. DenySecrets / BlockSensitiveQueryString
4. Routing
5. Authentication/Authorization
6. RateLimiter
7. ProblemDetails
8. Audit

---

## License

MIT (use it freely).
