# API Key Authentication in ASP.NET Core (.NET 10) - Complete Guide

A weather widget on someone else's website calls my API a million times a day. There's no human at the keyboard, no OAuth flow, no refresh token rotation - just a server somewhere quietly hitting `/forecast` every minute. JWT is overkill. Cookies don't apply. What that caller actually needs is a long-lived, identifiable, revocable secret that says "I'm this client, let me through." That's what an **API key** is - and getting it right in .NET 10 is the difference between a clean integration and a 2 AM page when one of those keys ends up on a public Pastebin.

**API key authentication in ASP.NET Core .NET 10** is a request-header authentication scheme where the client sends a pre-shared secret (typically in the `X-API-Key` header) and the server validates it against a stored hash. The recommended implementation is a custom `AuthenticationHandler<T>` registered via `AddAuthentication("ApiKey")`, paired with EF Core 10 for a hashed, revocable key store and HybridCache for sub-millisecond validation on hot paths.

In this article, I'll walk through every layer of a production-grade implementation - from the 5-minute static-key quickstart, to a hashed and DB-backed key store with prefixes (`sk_live_…`), rotation, revocation, scoped permissions, HybridCache validation, Scalar OpenAPI integration, and audit logging. I'll also share my decision matrix for picking between API Key, JWT, OAuth, and mTLS so you stop wondering whether you've made the right call. The full source is on GitHub. Let's get into it.

> **TL;DR.** For ASP.NET Core .NET 10, implement API key authentication as a custom `AuthenticationHandler<ApiKeyAuthenticationOptions>` registered with `AddAuthentication("ApiKey")`. Read the key from the `X-API-Key` header. Store keys in a database as **SHA-256 hashes**, never plaintext - and use a prefix convention (`sk_live_…`) so leaked keys are identifiable in logs. Compare hashes with `CryptographicOperations.FixedTimeEquals` to prevent timing attacks. Cache validation with HybridCache (TTL 60-300 seconds) for sub-millisecond hot-path lookups. Return [RFC 9457 ProblemDetails](https://datatracker.ietf.org/doc/html/rfc9457) with HTTP 401 when no key is provided and HTTP 403 when the key is valid but lacks the required scope. Use API keys for **server-to-server** clients; for human users, use JWT or OAuth. Skip middleware-only implementations - `AuthenticationHandler<T>` plugs into `[Authorize]`, OpenAPI, and the rest of the ASP.NET Core auth pipeline for free.

> **Pick your level.** This is a long guide on purpose - it's meant to be the canonical reference for API keys in .NET 10. You don't have to read it top to bottom:
> - **New to API key auth?** Start with [What Is API Key Authentication](#what-is-api-key-authentication) and the [Decision Matrix](#api-key-vs-jwt-vs-oauth-vs-mtls---my-decision-matrix), then run the [5-minute Quickstart](#quickstart-static-key-with-authenticationhandlert).
> - **Shipping API keys to real users?** Jump to [Production-Grade: Hashed, DB-Backed Keys](#production-grade-hashed-db-backed-api-keys) for hashing, prefixes, HybridCache, and the full source.
> - **Already in production?** Skim [Common Mistakes I See in Production](#common-mistakes-i-see-in-production), [Key Rotation Without Downtime](#key-rotation-without-downtime), and the [Production Checklist](#api-key-authentication-production-checklist).
>
> Either way, bookmark it. You'll come back to it.

## What Is API Key Authentication?

**API key authentication** is a scheme where a client identifies itself to a server by presenting a pre-shared secret string. The server compares the secret against a stored value (or hash of one) and either lets the request through or rejects it. The key answers a single question: *which application is calling?* It does not, by itself, identify a human user.

Three things make API key authentication distinctive:

- **Long-lived by design**: keys are typically valid for months or years, not minutes. There is no refresh flow.
- **Bearer credential**: anyone holding the key can use it. There is no proof-of-possession (PoP) layer like in mTLS or DPoP.
- **Application identity, not user identity**: an API key says "I'm WeatherWidget Inc.", not "I'm Mukesh logged in at 3:14 PM."

That last point is the one most teams misuse. API keys are great for *machine-to-machine* (M2M) traffic - a partner backend calling your API, an internal cron job, a webhook receiver. They are wrong for *user-facing* logins where you need to know who clicked the button. For that, use JWT, cookies, or OAuth.

> The OWASP API Security Top 10 (2023) ranks **API2:2023 Broken Authentication** as the second most critical API risk. Plaintext API key storage and missing rotation are explicit examples cited under it. We'll fix both in this article.

## API Key vs JWT vs OAuth vs mTLS - My Decision Matrix

This is the table I wish every API article led with. Most "implement API key auth" tutorials forget to ask whether you should be using API keys at all. Use this matrix to decide before you write a line of auth code.

| Criterion | API Key | JWT (Bearer) | OAuth 2.0 / OIDC | mTLS | Cookie |
|-----------|---------|--------------|-------------------|------|--------|
| **Caller type** | Server-to-server, scripts, IoT | Server or user (any) | User-facing apps with delegation | Server-to-server (high security) | Browser users |
| **Identifies a user?** | No (app only) | Yes | Yes | No (cert subject only) | Yes |
| **Lifetime** | Months/years | Minutes/hours | Minutes (access) + days (refresh) | Cert validity (years) | Session/days |
| **Revocation cost** | Low (DB flag flip) | Hard (must wait for expiry or use blacklist) | Hard (revoke refresh token) | Hard (cert revocation lists) | Easy (clear session) |
| **Scope granularity** | Per-key (custom claims) | Per-token claims | Rich scopes via `scope` claim | None natively | Per-session |
| **Transport** | `X-API-Key` header | `Authorization: Bearer` | `Authorization: Bearer` | TLS handshake | `Cookie` header |
| **Refresh flow needed** | No | Yes (or short re-login) | Yes (refresh token) | No | No |
| **Library footprint in .NET 10** | ~50 lines (custom handler) | `Microsoft.AspNetCore.Authentication.JwtBearer` | OpenIddict / Duende | ASP.NET Core built-in | `AddCookie()` |
| **Best for** | Webhooks, partner APIs, CLIs, IoT | SPAs, mobile apps, microservices | Third-party app delegation | Bank-to-bank, regulated industries | Server-rendered MVC apps |

**My take**: If your caller is a *server*, lean API Key. If your caller is a *human*, lean JWT or OAuth. The rest is just nuance. Most production systems mix two: OAuth for users, API keys for partner integrations and webhooks. That's the pattern Stripe, GitHub, and most major SaaS APIs follow.

## Anatomy of an API Key

Before writing code, let's nail down what an API key actually looks like in 2026. The OpenAPI 3.1 specification defines an `apiKey` security scheme that can be transported via `header`, `query`, or `cookie`. In practice, only one is acceptable today.

**Use a header. Always a header.** Never a query string.

Why not query strings:
- They land in **server access logs** verbatim.
- They land in **browser history** when the URL is opened.
- They land in **referer headers** if the response triggers a navigation.
- CDNs and proxies cache URLs - a cached GET response includes the key in the cache key.

The de-facto header name is `X-API-Key`. It is not a formal HTTP standard - the `X-` prefix was actually deprecated by [RFC 6648](https://datatracker.ietf.org/doc/html/rfc6648) - but it has become the convention because of widespread adoption (AWS API Gateway, GitHub before tokens, most webhook providers). Some APIs use the `Authorization` header with a custom scheme like `Authorization: ApiKey <key>`. Both work; pick one and stick with it.

### What goes inside the key string

A good API key has three components:

```
sk_live_<your-api-key>
└──┬──┘└────────────────┬───────────────────────┘
   │                    │
   prefix         random secret (160-256 bits)
```

- **Prefix** (`sk_live_`, `sk_test_`, `pk_live_`): identifies the key type at a glance. `sk_` for secret keys, `pk_` for publishable keys, `_live` for production, `_test` for sandbox. This pattern - popularized by Stripe - is now widely adopted by GitHub, OpenAI, and Anthropic. The prefix is **safe to log** because it does not leak the secret. GitHub's secret scanner uses prefixes like `ghp_` to detect accidentally committed tokens.
- **Random secret**: at least **128 bits** of entropy, ideally **256 bits**. Generated with `RandomNumberGenerator.GetBytes()` from `System.Security.Cryptography` and Base64URL-encoded.
- **Total length**: typically 32-48 characters. Long enough that brute-force is computationally infeasible, short enough that engineers can copy-paste without errors.

Here's how I generate keys in .NET 10:

```csharp
using System.Security.Cryptography;

public static string GenerateApiKey(string prefix = "sk_live_")
{
    Span<byte> bytes = stackalloc byte[32]; // 256 bits
    RandomNumberGenerator.Fill(bytes);
    var secret = Convert.ToBase64String(bytes)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');
    return $"{prefix}{secret}";
}
```

`RandomNumberGenerator.Fill` uses the OS cryptographic random source - on Linux it reads from `/dev/urandom`, on Windows from `BCryptGenRandom`. **Do not use `Random` or `Guid.NewGuid()` for keys.** `Random` is predictable; `Guid` only has 122 bits of entropy and the format is recognizable.

## Four Ways to Implement API Key Authentication in ASP.NET Core

There are four mainstream patterns for wiring API key validation into a request. Every other tutorial picks one and goes. Let's compare all four with a clear verdict.

| Approach | Where it runs | Plugs into `[Authorize]`? | OpenAPI security scheme? | Endpoint-level opt-in/out? | Verdict |
|----------|---------------|----------------------------|---------------------------|-----------------------------|---------|
| **Middleware** | Whole pipeline | No | No (manual) | Hard (manual path filter) | **Skip.** Globally invasive, hard to scope, doesn't integrate with auth pipeline. |
| **Authorization Filter** (`IAuthorizationFilter`) | Per-controller / per-action | Indirectly | No | Yes (attribute) | MVC-only. Works but doesn't work for Minimal APIs. |
| **Endpoint Filter** (`IEndpointFilter`) | Per-endpoint or per-group | No (custom) | Manual | Yes (`.AddEndpointFilter`) | Fine for one-off endpoints, but no integration with the auth system. |
| **`AuthenticationHandler<T>`** | Auth middleware | **Yes** | **Yes** (via `securitySchemes`) | Yes (`[Authorize(AuthenticationSchemes = "ApiKey")]`) | **Recommended.** |

**My take**: use `AuthenticationHandler<TOptions>`. Skip the others unless you cannot register an authentication scheme for some reason - and that almost never happens. Here's why this approach wins on every axis that matters:

1. **You get `[Authorize]` for free.** The handler creates a `ClaimsPrincipal`; ASP.NET Core's authorization pipeline runs as it would for JWT or cookies.
2. **OpenAPI integration is one line.** Scalar and Swagger UI both show an "Authorize" button when you register the scheme correctly.
3. **You get policies for free.** Per-key scopes (e.g., `keys:read`, `keys:admin`) become standard `[Authorize(Policy = "...")]` calls.
4. **Mixing schemes is trivial.** API keys for partners, JWT for users, all in the same app, no middleware ordering games.

The middleware approach is so commonly recommended in older articles that it's worth being explicit: **don't use it for new code in 2026**. It bypasses the auth pipeline, doesn't compose with `[Authorize]`, and forces you to reinvent things ASP.NET Core already does well.

## Quickstart: Static Key with `AuthenticationHandler<T>`

Let's start with the simplest possible production-shaped implementation. One static key, validated by an `AuthenticationHandler`. This is enough for an internal API or an MVP. The next section upgrades it to a hashed, multi-tenant key store - but understanding this version first makes the upgrade trivial.

### Project setup

```bash
dotnet new web -n ApiKeyAuth.Api
cd ApiKeyAuth.Api
dotnet add package Microsoft.AspNetCore.OpenApi --version 10.0.0
dotnet add package Scalar.AspNetCore --version 2.11.9
```

### The options class

```csharp
using Microsoft.AspNetCore.Authentication;

namespace ApiKeyAuth.Api.Authentication;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public const string HeaderName = "X-API-Key";
}
```

Subclassing `AuthenticationSchemeOptions` is the standard pattern. The constants live alongside so I can reference `ApiKeyAuthenticationOptions.DefaultScheme` instead of magic strings.

### The handler

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ApiKeyAuth.Api.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(
                ApiKeyAuthenticationOptions.HeaderName, out var providedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expectedKey = configuration["ApiKey:Value"];
        if (string.IsNullOrEmpty(expectedKey))
        {
            return Task.FromResult(
                AuthenticateResult.Fail("API key is not configured on the server."));
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedKey.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);

        if (providedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "static-client"),
            new Claim("client_id", "static-client")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

The two non-obvious lines are worth highlighting:

- **`AuthenticateResult.NoResult()`** when the header is missing. This signals "no decision" rather than "fail" - it lets other auth schemes try if you have multiple registered. If you return `Fail` here, mixing schemes becomes painful.
- **`CryptographicOperations.FixedTimeEquals`** for the comparison. A naive `string.Equals` returns as soon as the first character differs. An attacker measuring response times can derive the key one byte at a time. `FixedTimeEquals` always takes the same time regardless of where the bytes diverge - this is a [timing attack mitigation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals).

### Wire it up in `Program.cs`

```csharp
using ApiKeyAuth.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services
    .AddAuthentication(ApiKeyAuthenticationOptions.DefaultScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.DefaultScheme, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/public", () => "anyone can read this");

app.MapGet("/secure", () => "only callers with a valid API key see this")
   .RequireAuthorization();

app.Run();
```

Configure the static key in `appsettings.Development.json` (move to user-secrets / environment variables for anything beyond local):

```json
{
  "ApiKey": {
    "Value": "sk_live_<your-api-key>"
  }
}
```

That's a working API key authentication setup in roughly 60 lines. Test it:

```bash
# Should return 401
curl http://localhost:5000/secure

# Should return the message
curl -H "X-API-Key: sk_live_<your-api-key>" http://localhost:5000/secure
```

This works, and for a single trusted client it's even fine in production. **But it falls apart the moment you have two clients.** Adding clients means redeploying. Revoking a key means redeploying. There is no audit trail. And the key is in `appsettings.json` - which is the most common way API keys end up on Pastebin.

## Production-Grade: Hashed, DB-Backed API Keys

The real implementation has six properties:

1. Each client gets a **unique key**.
2. Keys are **hashed** in the database - the plaintext is never stored.
3. The plaintext is **shown to the client exactly once** at issuance.
4. Each key has **expiry** and **explicit revocation**.
5. Each key carries **scopes** (granular permissions).
6. Each request **updates a "last used" timestamp** for visibility.

### The `ApiKey` entity

```csharp
namespace ApiKeyAuth.Api.Entities;

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // The first ~12 chars of the plaintext key (e.g., "sk_live_3K7s")
    // Stored so I can index lookups and surface non-secret hints in audit logs
    public string Prefix { get; set; } = default!;

    // SHA-256 hash of the FULL plaintext key, hex-encoded
    public string KeyHash { get; set; } = default!;

    public string Name { get; set; } = default!;
    public string OwnerId { get; set; } = default!;

    // Comma-separated scopes ("keys:read", "keys:admin")
    public string Scopes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public bool IsActive(TimeProvider time) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > time.GetUtcNow());
}
```

### Why hash, and why SHA-256?

Hashing API keys serves the same purpose as hashing passwords: if your database is exfiltrated, the keys are not directly usable. The thief gets hashes, not bearer credentials.

For passwords, you want **slow** hashes (Argon2id, PBKDF2 with high iterations) because passwords are low-entropy and humans pick "password123". For API keys, you want **fast** hashes (SHA-256) because:

- **Entropy is high.** A 256-bit random key is computationally infeasible to brute-force regardless of hash speed. Slow hashing buys you nothing.
- **Validation runs on every request.** Argon2id at 100ms per validation would cap your API throughput at ~10 RPS per core. SHA-256 takes microseconds.
- **No salt is needed.** Each key is already random and unique; salt protects against rainbow tables for predictable inputs (passwords). Random keys aren't predictable.

```csharp
using System.Security.Cryptography;
using System.Text;

public static class ApiKeyHasher
{
    public static string Hash(string plaintextKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey), hash);
        return Convert.ToHexString(hash);
    }

    public static bool Verify(string plaintextKey, string storedHash)
    {
        var computed = Hash(plaintextKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
```

`SHA256.HashData` is the one-shot static API on the `SHA256` class - faster and lower-allocation than the older `SHA256.Create()` instance pattern, and the [CA1850](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1850) analyzer recommends it. `FixedTimeEquals` again, for the same timing-attack reason.

### EF Core configuration

```csharp
using ApiKeyAuth.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApiKeyAuth.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Prefix).IsRequired().HasMaxLength(20);
            entity.Property(k => k.KeyHash).IsRequired().HasMaxLength(64);
            entity.Property(k => k.Name).IsRequired().HasMaxLength(100);
            entity.Property(k => k.OwnerId).IsRequired().HasMaxLength(100);
            entity.Property(k => k.Scopes).HasMaxLength(500);

            // KeyHash is the actual lookup column - unique, indexed
            entity.HasIndex(k => k.KeyHash).IsUnique();

            // Prefix index speeds up admin queries ("show me sk_live_3K7s...")
            entity.HasIndex(k => k.Prefix);
        });
    }
}
```

The unique index on `KeyHash` is what makes lookups O(log n) on the database side. The non-unique prefix index is purely for admin/audit queries - "find me the key starting with `sk_live_3K7s`."

### Issuing a new key

```csharp
public sealed record IssueApiKeyRequest(string Name, string OwnerId, string[] Scopes, int? TtlDays);
public sealed record IssueApiKeyResponse(Guid Id, string Name, string PlaintextKey, string Prefix, DateTime? ExpiresAt);

app.MapPost("/admin/keys", async (
    IssueApiKeyRequest request,
    AppDbContext db,
    TimeProvider time,
    CancellationToken ct) =>
{
    var plaintext = ApiKeyGenerator.Generate("sk_live_");

    var entity = new ApiKey
    {
        Prefix = plaintext[..12],
        KeyHash = ApiKeyHasher.Hash(plaintext),
        Name = request.Name,
        OwnerId = request.OwnerId,
        Scopes = string.Join(',', request.Scopes),
        ExpiresAt = request.TtlDays is { } days
            ? time.GetUtcNow().AddDays(days).UtcDateTime
            : null
    };

    db.ApiKeys.Add(entity);
    await db.SaveChangesAsync(ct);

    return Results.Created(
        $"/admin/keys/{entity.Id}",
        new IssueApiKeyResponse(entity.Id, entity.Name, plaintext, entity.Prefix, entity.ExpiresAt));
})
.RequireAuthorization("admin");
```

The plaintext key is returned **exactly once** in the `IssueApiKeyResponse`. The client must store it - I cannot regenerate it because I don't have it anymore (only the hash). This is the same UX as GitHub personal access tokens: copy now, or generate a new one.

I'm using `TimeProvider.GetUtcNow()` rather than `DateTime.UtcNow` because `TimeProvider` is the .NET 8+ abstraction that makes time mockable in tests - the test project uses `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` to test expiry without `Thread.Sleep`.

### The production handler

```csharp
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyValidator validator)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(
                ApiKeyAuthenticationOptions.HeaderName, out var providedKey))
        {
            return AuthenticateResult.NoResult();
        }

        var key = providedKey.ToString();
        var result = await validator.ValidateAsync(key, Context.RequestAborted);

        if (!result.IsValid)
        {
            Logger.LogWarning(
                "API key authentication failed for prefix {Prefix}: {Reason}",
                result.Prefix ?? "(none)", result.Reason);
            return AuthenticateResult.Fail(result.Reason ?? "Invalid API key.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.KeyId!.Value.ToString()),
            new(ClaimTypes.Name, result.Name!),
            new("client_id", result.OwnerId!),
            new("api_key_prefix", result.Prefix!)
        };
        claims.AddRange(result.Scopes.Select(s => new Claim("scope", s)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
```

The handler delegates the lookup to an `IApiKeyValidator`. That separation is what lets me layer caching on without touching the handler.

### The validator and the cache

```csharp
public sealed record ApiKeyValidationResult(
    bool IsValid,
    string? Reason,
    Guid? KeyId,
    string? Name,
    string? OwnerId,
    string? Prefix,
    string[] Scopes)
{
    public static ApiKeyValidationResult Invalid(string reason, string? prefix = null) =>
        new(false, reason, null, null, null, prefix, []);
}

public interface IApiKeyValidator
{
    Task<ApiKeyValidationResult> ValidateAsync(string plaintextKey, CancellationToken ct);
}

public sealed class ApiKeyValidator(
    AppDbContext db,
    HybridCache cache,
    TimeProvider time) : IApiKeyValidator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public async Task<ApiKeyValidationResult> ValidateAsync(
        string plaintextKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
            return ApiKeyValidationResult.Invalid("API key is empty.");

        var prefix = plaintextKey.Length >= 12 ? plaintextKey[..12] : plaintextKey;
        var hash = ApiKeyHasher.Hash(plaintextKey);

        // Cache by hash so we never put the plaintext in the cache
        var cached = await cache.GetOrCreateAsync(
            $"apikey:{hash}",
            async cancel => await LookupAsync(hash, cancel),
            new HybridCacheEntryOptions { Expiration = CacheTtl },
            cancellationToken: ct);

        if (cached is null)
            return ApiKeyValidationResult.Invalid("API key not found.", prefix);

        if (cached.RevokedAt is not null)
            return ApiKeyValidationResult.Invalid("API key has been revoked.", prefix);

        if (cached.ExpiresAt is { } exp && exp <= time.GetUtcNow())
            return ApiKeyValidationResult.Invalid("API key has expired.", prefix);

        // Fire-and-forget last-used update so we don't block the request
        _ = TouchLastUsedAsync(cached.Id);

        return new ApiKeyValidationResult(
            true, null, cached.Id, cached.Name, cached.OwnerId,
            cached.Prefix, cached.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<ApiKey?> LookupAsync(string hash, CancellationToken ct)
        => await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct);

    private async Task TouchLastUsedAsync(Guid keyId)
    {
        try
        {
            await db.ApiKeys
                .Where(k => k.Id == keyId)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(k => k.LastUsedAt, time.GetUtcNow().UtcDateTime));
        }
        catch
        {
            // Telemetry path - never fail the request because audit failed
        }
    }
}
```

Two observations:

- **The cache key is the hash, not the plaintext.** I never want the plaintext key sitting in process memory longer than necessary, especially in a distributed cache where it could leak via dump or telemetry.
- **`ExecuteUpdateAsync` for last-used.** It's a single SQL `UPDATE` with no entity hydration, no change tracking, no roundtrip. Fire-and-forget is fine here because losing a last-used timestamp on a crash is acceptable; blocking the request to write it is not.

### HybridCache: the latency win

[`HybridCache`](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid) is a .NET 9+ caching primitive that combines in-memory (L1) and distributed (L2) caches with built-in stampede protection. For API key validation, the latency math matters:

| Lookup path | Approximate latency | Throughput at 1ms p99 budget |
|-------------|---------------------|-------------------------------|
| In-memory cache hit (L1) | **~50 ns** | ~20,000,000 ops/sec |
| Distributed cache hit (L2, Redis local) | ~500 µs | ~2,000 ops/sec |
| Database lookup (PostgreSQL, indexed) | ~1-3 ms | ~300-1,000 ops/sec |

Numbers are order-of-magnitude estimates from typical .NET microbenchmarks - L1 cache reads in nanoseconds, network roundtrips in hundreds of microseconds, indexed DB reads in low milliseconds. The exact numbers don't matter; the **ratios** do. An L1 hit is roughly **20,000x to 60,000x faster than a database roundtrip**. On any API doing more than a handful of requests per second, hot-key validation must not touch the database.

The trade-off is **revocation latency**. Revoking a key with a 2-minute TTL means the revocation can take up to 2 minutes to propagate to all instances. For most applications that is fine - you're not racing the attacker to the millisecond. For high-security cases, drop the TTL to 30 seconds, or skip the L1 cache for revoked-key checks specifically. There is no free lunch here; pick where you accept latency.

### Wiring the production version

```csharp
using ApiKeyAuth.Api.Authentication;
using ApiKeyAuth.Api.Data;
using ApiKeyAuth.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

#pragma warning disable EXTEXP0018 // HybridCache is in preview
builder.Services.AddHybridCache();
#pragma warning restore EXTEXP0018

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IApiKeyValidator, ApiKeyValidator>();

builder.Services
    .AddAuthentication(ApiKeyAuthenticationOptions.DefaultScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.DefaultScheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("keys:read", p => p.RequireClaim("scope", "keys:read"))
    .AddPolicy("keys:admin", p => p.RequireClaim("scope", "keys:admin"));

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.UseAuthentication();
app.UseAuthorization();

app.Run();
```

`AddAuthorizationBuilder` is the .NET 8+ fluent API for policies - it's terser than the older `AddAuthorization(o => o.AddPolicy(...))` form and what Microsoft Learn now recommends. `TimeProvider.System` is the production implementation; tests substitute `FakeTimeProvider`.

## Authorization After Authentication: Per-Key Scopes

A bare `[Authorize]` only checks "is this caller authenticated?" Real APIs need finer-grained checks. With per-key scopes baked into claims, ASP.NET Core's policy system handles this naturally:

```csharp
app.MapGet("/keys", async (AppDbContext db, CancellationToken ct) =>
        await db.ApiKeys.AsNoTracking().Select(k => new
        {
            k.Id, k.Prefix, k.Name, k.OwnerId, k.CreatedAt, k.ExpiresAt, k.LastUsedAt
        }).ToListAsync(ct))
   .RequireAuthorization("keys:read");

app.MapDelete("/keys/{id:guid}", async (
        Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) =>
{
    var rows = await db.ApiKeys
        .Where(k => k.Id == id)
        .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, time.GetUtcNow().UtcDateTime), ct);
    return rows == 0 ? Results.NotFound() : Results.NoContent();
})
.RequireAuthorization("keys:admin");
```

A key issued with scope `keys:read` can list keys but not revoke them. A key with `keys:admin` can do both. The same key can be issued with multiple scopes - just include them all in the comma-separated `Scopes` field at issuance time.

## Returning Proper 401 / 403 with ProblemDetails

The default ASP.NET Core 401 response is an empty body with the status code. For a public API, you want something machine-readable. RFC 9457 defines [ProblemDetails](https://datatracker.ietf.org/doc/html/rfc9457) - the standard JSON error format - and ASP.NET Core has built-in support.

The status code distinction matters:
- **401 Unauthorized** = "I don't know who you are." No key, or invalid key.
- **403 Forbidden** = "I know who you are, but you can't do this." Key is valid but lacks the required scope.

I get the right status codes for free by registering the auth scheme correctly. To attach ProblemDetails to the responses, override the auth events and use the built-in `IProblemDetailsService`:

```csharp
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public const string HeaderName = "X-API-Key";
}

// In Program.cs - register a global ProblemDetails writer
builder.Services.AddProblemDetails();

// In the handler - override HandleChallengeAsync (401) and HandleForbiddenAsync (403)
protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
{
    Response.StatusCode = StatusCodes.Status401Unauthorized;
    Response.ContentType = "application/problem+json";

    var problemDetailsService = Context.RequestServices
        .GetRequiredService<IProblemDetailsService>();

    await problemDetailsService.WriteAsync(new ProblemDetailsContext
    {
        HttpContext = Context,
        ProblemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "A valid API key is required. Send it in the X-API-Key header.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2"
        }
    });
}
```

Now a request without a key returns:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "detail": "A valid API key is required. Send it in the X-API-Key header."
}
```

Clients that consume RFC 9457 (which is the default in modern HTTP libraries) can parse and surface this structurally.

## Documenting API Key Auth in OpenAPI 3.1 + Scalar

ASP.NET Core .NET 10 ships with built-in OpenAPI 3.1 generation (no Swashbuckle needed). To make Scalar's UI render an "Authorize" button for the `X-API-Key` header, add a document transformer that registers the security scheme:

```csharp
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

internal sealed class ApiKeySecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["ApiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = ApiKeyAuthenticationOptions.HeaderName,
                In = ParameterLocation.Header,
                Description = "API key sent in the X-API-Key header."
            }
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = schemes;

        if (document.Paths is null) return Task.CompletedTask;

        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
        {
            operation.Value.Security ??= [];
            operation.Value.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
            });
        }

        return Task.CompletedTask;
    }
}

// In Program.cs
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>();
});
```

> **Why the unfamiliar shape?** [Microsoft.OpenApi 2.x](https://www.nuget.org/packages/Microsoft.OpenApi) (the version that ships with `Microsoft.AspNetCore.OpenApi 10.0`) reorganized its types into the root `Microsoft.OpenApi` namespace and removed the `Reference` property from `OpenApiSecurityScheme`. References now go through dedicated reference types like `OpenApiSecuritySchemeReference`. If you're following an older guide that uses `Microsoft.OpenApi.Models` and `OpenApiReference`, that's the .NET 9 pattern - the code above is the .NET 10 equivalent confirmed against the [official Microsoft Learn docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/customize-openapi?view=aspnetcore-10.0).

Now Scalar renders an "Authorize" button. The user pastes the key once, and every "Try it" call from that point on includes the `X-API-Key` header automatically. This is the demo-day moment that makes API keys feel as professional as JWT.

## Audit Logging API Key Usage

The whole point of database-backed keys is that I can answer questions like "who called the API at 3:14 AM" and "is this key still in use?" The handler already logs failures. To capture successful calls, layer Serilog request logging with an enricher that pulls the key prefix and ID from the principal:

```csharp
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (ctx, http) =>
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            ctx.Set("ApiKeyPrefix", http.User.FindFirst("api_key_prefix")?.Value);
            ctx.Set("ClientId", http.User.FindFirst("client_id")?.Value);
        }
    };
});
```

Every request line now includes `ApiKeyPrefix` and `ClientId`. The prefix is **safe to log** (it's not a secret); the full key never touches a log line. If a client says "we got a 403 at 09:42 UTC", I can search Serilog/Seq for `ApiKeyPrefix = sk_live_3K7s` and find the exact request.

## Key Rotation Without Downtime

API keys are long-lived, but "long-lived" should not mean "never replaced." Rotate keys on a schedule (90 days for high-sensitivity, 1 year for partner integrations) and on every credential exposure (employee leaves, key in a screenshot, etc.).

The grace-period rotation flow:

1. **Issue new key** for the same `OwnerId` with the same scopes. Both keys are now active.
2. **Hand the new key to the client.** They begin using it.
3. **Wait** for the client to confirm migration (manual confirmation, or a few hundred successful calls with the new key in your audit log).
4. **Revoke the old key** by setting `RevokedAt`.
5. **Watch for failures** - any client still using the old key throws 401 within the cache TTL window.

This is exactly how Stripe handles key rotation. The data model already supports it - nothing in `ApiKey` says one client can have only one key. Issue as many as you need; revoke them independently.

```csharp
app.MapPost("/admin/keys/{id:guid}/rotate", async (
    Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) =>
{
    var existing = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
    if (existing is null) return Results.NotFound();

    var plaintext = ApiKeyGenerator.Generate("sk_live_");
    var newKey = new ApiKey
    {
        Prefix = plaintext[..12],
        KeyHash = ApiKeyHasher.Hash(plaintext),
        Name = $"{existing.Name} (rotated {time.GetUtcNow():yyyy-MM-dd})",
        OwnerId = existing.OwnerId,
        Scopes = existing.Scopes,
        ExpiresAt = existing.ExpiresAt
    };
    db.ApiKeys.Add(newKey);
    await db.SaveChangesAsync(ct);

    // Old key still active here - caller revokes it manually after migration
    return Results.Created(
        $"/admin/keys/{newKey.Id}",
        new IssueApiKeyResponse(newKey.Id, newKey.Name, plaintext, newKey.Prefix, newKey.ExpiresAt));
})
.RequireAuthorization("keys:admin");
```

## Common Mistakes I See in Production

These are the patterns that show up in code review on every team I've worked with. Avoiding them is the actual point of this article.

1. **Storing keys in `appsettings.json` and committing it.** The number-one way keys end up on Pastebin. Use environment variables, user-secrets, or a secret manager (Azure Key Vault, AWS Secrets Manager).
2. **Storing plaintext keys in the database.** A leaked DB dump is a leaked credential set. Hash everything.
3. **Using `string.Equals` for comparison.** Timing attack. Use `CryptographicOperations.FixedTimeEquals`.
4. **Logging the full key.** Even at `Debug` level, even in error messages. The prefix is safe; the rest is the secret. If you must log the key for debugging, redact: `sk_live_3K7s********************`.
5. **Putting the key in the URL.** Query strings end up in access logs, browser history, and CDN caches. Always header.
6. **No expiry.** Keys without expiry rot. They're often handed to a vendor, the vendor's product changes, the key becomes someone's debugging shortcut. Set a TTL even for "permanent" keys - 1 year is a good default.
7. **No rotation.** Even with hashing, a key that's been live for 5 years has had 5 years of opportunity to leak. Rotate on a schedule.
8. **Using `AddAuthentication()` without specifying a default scheme.** If JWT is also registered and `Authorization: Bearer ...` arrives, the auth pipeline tries the wrong scheme. Be explicit.
9. **Caching the plaintext key in HybridCache.** Cache by hash. The plaintext should live in the request and then disappear.
10. **Not testing 401 vs 403 separately.** Most teams test "happy path + invalid key." They miss "valid key, wrong scope" - which is its own bug class.

## API Key Authentication Production Checklist

Before shipping to production:

- [ ] Keys are at least 128 bits of entropy (256 bits preferred), generated with `RandomNumberGenerator`.
- [ ] Plaintext keys are **shown to the client exactly once** at issuance.
- [ ] The database stores **only SHA-256 hashes**, never plaintext.
- [ ] Comparison uses `CryptographicOperations.FixedTimeEquals`.
- [ ] Keys have a **prefix** that identifies type (`sk_live_`, `sk_test_`).
- [ ] Keys have an **`ExpiresAt`** (even if it's 1 year out).
- [ ] Keys can be **revoked** without redeploy (`RevokedAt` flag).
- [ ] Validation runs through **HybridCache** with a 30-300 second TTL appropriate to your revocation latency tolerance.
- [ ] Failed auth returns **HTTP 401**; insufficient scope returns **HTTP 403**, both as **ProblemDetails (RFC 9457)**.
- [ ] OpenAPI document includes the `apiKey` security scheme; Scalar/Swagger UI shows an Authorize button.
- [ ] Logs include `ApiKeyPrefix` and `ClientId` on every request, never the plaintext.
- [ ] Key rotation flow is documented and tested.
- [ ] HTTPS is enforced (`UseHttpsRedirection` + HSTS); HTTP requests are rejected.

## Key Takeaways

- **Use `AuthenticationHandler<TOptions>`**, not middleware-only or filter-only patterns. It's the only approach that integrates with `[Authorize]`, OpenAPI, and the rest of the ASP.NET Core auth pipeline.
- **Hash keys with SHA-256** in the database. Slow hashes (Argon2id, PBKDF2) buy nothing for high-entropy random keys and would tank validation throughput.
- **Always compare with `CryptographicOperations.FixedTimeEquals`** - timing attacks on string comparison are a real risk on internet-exposed APIs.
- **Use a prefix convention** (`sk_live_…`) so leaked keys are scannable and the prefix is safe to log.
- **Cache validation with HybridCache.** L1 hits are roughly 20,000-60,000x faster than database roundtrips - on any non-trivial API, hot keys must not hit the DB on every call.
- **Return RFC 9457 ProblemDetails** for 401 and 403 with distinct status codes - 401 = no/invalid key, 403 = valid key but missing scope.

## Troubleshooting

**Always returns 401, even with the correct key** - The handler is wired up, but `UseAuthentication()` is not called before `UseAuthorization()`. With Minimal APIs, calling `RequireAuthorization()` is enough in most cases - but if you've explicitly added middleware, the order is `UseAuthentication` then `UseAuthorization`, then the endpoints. Reverse the order and you get 401 on every protected route.

**`AuthenticateResult.NoResult()` triggers a 500** - You returned `NoResult` from the handler, but no other auth scheme is registered to handle the request. Either return `Fail` instead, or set `DefaultAuthenticateScheme` and `DefaultChallengeScheme` explicitly when calling `AddAuthentication`.

**`HybridCache` is in preview, build warnings** - HybridCache (`Microsoft.Extensions.Caching.Hybrid`) is marked experimental in .NET 9 and 10. The build emits `EXTEXP0018`. Suppress it with `#pragma warning disable EXTEXP0018` around the registration if your team's policy treats warnings as errors. The API surface is stable for production use; the experimental flag is about minor breaking changes between minor versions.

**Key works in development but fails in production** - Almost always a configuration issue. The plaintext key in your dev `appsettings.Development.json` got committed to git or shared in chat, and the production key is different. Check that the deployed config has the right key and that the database in production has the row for that key's hash.

**Last-used timestamp never updates** - The fire-and-forget `TouchLastUsedAsync` swallows exceptions silently. Check your application logs - the most common cause is a `DbContext` lifetime issue (the scoped context is disposed before the fire-and-forget task runs). Switch to `IDbContextFactory<AppDbContext>` for the audit path, or accept that the timestamp is best-effort and move on.

**Two keys hash to the same value** - Statistically impossible for SHA-256 with random 256-bit inputs. If you see this, check that your `KeyHash` column has the unique index and that the issuer is not regenerating keys with a fixed seed.

## Summary

API key authentication in ASP.NET Core .NET 10 is genuinely simple to do well - a custom `AuthenticationHandler<TOptions>`, hashed keys in EF Core, HybridCache for the hot path, and ProblemDetails for honest errors. The pieces that separate "tutorial" from "production" are the boring ones: hashing, prefixes, rotation, and audit logging. None of them take more than a few hours to add, and all of them are what stand between you and a 2 AM page when a key shows up where it shouldn't.

Pick the right tool for the right caller. If your client is a server, lean API Key. If it's a human, lean [JWT](/blog/jwt-authentication-in-aspnet-core/) with [refresh tokens](/blog/refresh-tokens-in-aspnet-core/), or OAuth. If you're guessing, use the decision matrix in this article and pick deliberately.

Authentication only settles who the caller is. What they may do is a separate question, built up across [role-based](/blog/role-based-authorization-in-aspnet-core/), [claims-based](/blog/claims-based-authorization-in-aspnet-core/), [policy-based](/blog/policy-based-authorization-in-aspnet-core/), and [permission-based authorization](/blog/permission-based-authorization-in-aspnet-core/) - and API keys deliberately carry none of it, since a key identifies an application rather than a person.

The full source code, including a complete xUnit v3 integration test suite with `WebApplicationFactory` and `FakeTimeProvider` for deterministic expiry tests, lives in the [course repository on GitHub](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/05-api-security/api-key-authentication-aspnet-core). The demo runs against EF Core's in-memory provider so you can clone, `dotnet run`, and try it without setting up a database.

If you found this helpful, share it with your colleagues - and if there's a topic you'd like to see covered next, drop a comment and let me know. Don't forget to subscribe to the [newsletter](https://codewithmukesh.com/newsletter/) for weekly .NET content with judgment calls, benchmarks, and real-world patterns - not just tutorials.

Happy Coding :)
