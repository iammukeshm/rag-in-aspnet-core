# Refresh Tokens in ASP.NET Core - A Complete .NET 10 Guide

A JWT has one annoying property: once you hand it out, you cannot take it back. To limit the damage if a token leaks, you make it short-lived - 15 minutes, maybe an hour. But that creates a new problem: you do not want to force users to log in again every 15 minutes. Refresh tokens solve exactly this.

This is Part 2 of my guide on securing ASP.NET Core APIs. In [Part 1](/blog/jwt-authentication-in-aspnet-core/) I built JWT authentication. Here I will add refresh tokens on top of it in .NET 10 - with token rotation, reuse detection, and revocation, which are the parts most tutorials skip. Everything is built on [Minimal APIs](/blog/minimal-apis-aspnet-core/) and runs out of the box with zero database setup. Let's get into it.

## TL;DR - Refresh Tokens in ASP.NET Core .NET 10

**To implement refresh tokens in ASP.NET Core .NET 10:** on login, return a short-lived JWT access token (15 minutes) plus a long-lived refresh token - a cryptographically random string generated with `RandomNumberGenerator.GetBytes` and stored in the database. When the access token expires, the client posts the refresh token to a `/refresh` endpoint, which validates it, **rotates** it (revokes the old one and issues a new one), and returns a fresh access token. If a revoked refresh token is ever used again, treat it as theft and revoke the user's entire token family.

This guide builds directly on [JWT authentication](/blog/jwt-authentication-in-aspnet-core/) (Part 1). If you have not set that up yet, start there.

## Why JWTs Need Refresh Tokens

A JWT is stateless. The server does not store it - it just validates the signature and the expiry on each request. That is what makes JWTs fast and easy to scale, but it has a cost: **you cannot revoke a JWT before it expires.** If an attacker steals one, it works until it runs out, and there is no built-in way to shut it off.

The defense is to keep access tokens short-lived. But a 15-minute login that logs people out constantly is terrible UX. So you pair two tokens:

- **Access token** - a short-lived JWT (15 to 60 minutes) sent on every request.
- **Refresh token** - a long-lived random string (days or weeks) used only to get a new access token.

The client quietly swaps an expired access token for a new one using the refresh token, and the user never notices. Because the refresh token lives in your database, you can revoke it whenever you want - which is the control a plain JWT does not give you.

## How the Refresh Token Flow Works

1. The user logs in. The API returns an access token and a refresh token, and saves the refresh token in the database.
2. The client calls protected endpoints with the access token.
3. The access token expires. The next call returns `401`.
4. The client posts its refresh token to `/api/auth/refresh`.
5. The API checks the refresh token is valid and active, **rotates** it (revokes the old, issues a new one), and returns a new access token plus the new refresh token.
6. The client stores the new pair and carries on.

That rotation step in #5 is the important one, and it is what we will get right.

## Modeling the Refresh Token

Unlike a JWT, a refresh token is stored server-side so we can expire and revoke it. Here is the entity:

```csharp title="Entities/RefreshToken.cs"
public class RefreshToken
{
    public int Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
    public DateTime? Revoked { get; set; }

    // When this token is rotated, we record the token that replaced it.
    public string? ReplacedByToken { get; set; }

    // A token is only usable if it has not been revoked and has not expired.
    public bool IsActive => Revoked is null && DateTime.UtcNow < Expires;
}
```

The `Revoked` and `ReplacedByToken` fields are what make rotation and reuse detection possible. Add a `DbSet` for it to your context:

```csharp title="Data/AppDbContext.cs"
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
}
```

## Generating a Refresh Token

A refresh token does not need any structure - it just needs to be impossible to guess. So I generate a large random value:

```csharp title="Auth/TokenService.cs"
public string CreateRefreshToken()
{
    // A refresh token is just a large cryptographically-random value.
    // RandomNumberGenerator replaces the obsolete RNGCryptoServiceProvider.
    var randomBytes = RandomNumberGenerator.GetBytes(64);
    return Convert.ToBase64String(randomBytes);
}
```

If you have followed older tutorials, you have probably seen `RNGCryptoServiceProvider` here. That type is obsolete from .NET 6 onwards - `RandomNumberGenerator.GetBytes` is the modern, simpler replacement.

## Issuing Tokens on Login

When the user logs in, I generate both tokens, save the refresh token row, and return the pair:

```csharp title="Endpoints/AuthEndpoints.cs"
private static async Task<IResult> LoginAsync(
    LoginRequest request,
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ITokenService tokenService,
    IOptions<JwtSettings> jwtSettings)
{
    var user = await userManager.FindByEmailAsync(request.Email);
    if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
    {
        return Results.Unauthorized();
    }

    var roles = await userManager.GetRolesAsync(user);
    var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, roles);

    // Issue a refresh token and store it so we can rotate or revoke it later.
    var refreshToken = tokenService.CreateRefreshToken();
    var refreshExpiresAt = DateTime.UtcNow.AddDays(jwtSettings.Value.RefreshTokenDays);

    db.RefreshTokens.Add(new RefreshToken
    {
        Token = refreshToken,
        UserId = user.Id,
        Created = DateTime.UtcNow,
        Expires = refreshExpiresAt
    });
    await db.SaveChangesAsync();

    return Results.Ok(new AuthResponse(
        user.Id, user.Email!, roles,
        accessToken, accessExpiresAt,
        refreshToken, refreshExpiresAt));
}
```

The access token generation (`CreateAccessToken`) is exactly what I built in [Part 1](/blog/jwt-authentication-in-aspnet-core/) - a signed JWT with the user's claims, using `JsonWebTokenHandler`. The only difference is I read the lifetime from an `AccessTokenMinutes` setting so I can keep it short.

## The Refresh Endpoint - With Rotation and Reuse Detection

This is the core of the whole article. When a refresh token comes in, I do four things: validate it, detect reuse, rotate it, and issue a new access token.

```csharp title="Endpoints/AuthEndpoints.cs"
private static async Task<IResult> RefreshAsync(
    RefreshRequest request,
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ITokenService tokenService,
    IOptions<JwtSettings> jwtSettings)
{
    var existing = await db.RefreshTokens
        .FirstOrDefaultAsync(t => t.Token == request.RefreshToken);

    // Token we have never seen - reject.
    if (existing is null)
    {
        return Results.Unauthorized();
    }

    // Token exists but is not usable. If it was already revoked, someone is
    // replaying an old token - assume it was stolen and revoke the whole family.
    if (!existing.IsActive)
    {
        if (existing.Revoked is not null)
        {
            await RevokeAllActiveTokensAsync(db, existing.UserId);
        }
        return Results.Unauthorized();
    }

    var user = await userManager.FindByIdAsync(existing.UserId);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    // Rotation: every refresh kills the old token and issues a brand-new one.
    var newRefreshToken = tokenService.CreateRefreshToken();
    var refreshExpiresAt = DateTime.UtcNow.AddDays(jwtSettings.Value.RefreshTokenDays);

    existing.Revoked = DateTime.UtcNow;
    existing.ReplacedByToken = newRefreshToken;

    db.RefreshTokens.Add(new RefreshToken
    {
        Token = newRefreshToken,
        UserId = user.Id,
        Created = DateTime.UtcNow,
        Expires = refreshExpiresAt
    });
    await db.SaveChangesAsync();

    var roles = await userManager.GetRolesAsync(user);
    var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, roles);

    return Results.Ok(new AuthResponse(
        user.Id, user.Email!, roles,
        accessToken, accessExpiresAt,
        newRefreshToken, refreshExpiresAt));
}
```

Let me unpack the two ideas that make this secure:

### Token rotation

Every time a refresh token is used, it is **immediately revoked and replaced**. A refresh token works exactly once. This shrinks the window an attacker has: a stolen refresh token is only useful until the real user (or the attacker) uses it once.

### Reuse detection

Here is the clever part. Because each token is single-use, a valid token should never be presented twice. So if a token that has already been **revoked** shows up again, something is wrong - the most likely explanation is that it was stolen and now two parties hold copies. When that happens, I revoke every active token for that user, forcing a fresh login:

```csharp title="Endpoints/AuthEndpoints.cs"
private static async Task RevokeAllActiveTokensAsync(AppDbContext db, string userId)
{
    var activeTokens = await db.RefreshTokens
        .Where(t => t.UserId == userId && t.Revoked == null)
        .ToListAsync();

    foreach (var token in activeTokens)
    {
        token.Revoked = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();
}
```

This is the single biggest difference between a toy implementation and a real one, and most tutorials leave it out.

## Revoking a Token (Logout)

Logout is just revoking the refresh token so it can never be exchanged again:

```csharp title="Endpoints/AuthEndpoints.cs"
private static async Task<IResult> RevokeAsync(RevokeRequest request, AppDbContext db)
{
    var token = await db.RefreshTokens
        .FirstOrDefaultAsync(t => t.Token == request.RefreshToken);

    if (token is null || !token.IsActive)
    {
        return Results.NotFound("Token not found or already inactive.");
    }

    token.Revoked = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok("Refresh token revoked.");
}
```

One important detail: the `/refresh` and `/revoke` endpoints are **not** protected with `.RequireAuthorization()`. They cannot be - the access token is usually expired by the time you call them. The refresh token itself is the credential. Because these routes are open, I put [rate limiting](/blog/rate-limiting-aspnet-core/) in front of them so nobody can hammer the refresh endpoint with guessed token values.

## Where Should You Store the Refresh Token?

This trips up a lot of people, so here is the honest comparison:

| Storage location | Pros | Cons |
|------------------|------|------|
| **httpOnly cookie** | Not readable by JavaScript, so it survives XSS attacks | Needs CSRF protection; harder for mobile clients |
| **Response body → app memory** | Simple, works for SPAs and mobile apps alike | Lost on page refresh unless persisted somewhere |
| **localStorage** | Survives refresh, easy to use | Readable by any script - one XSS and the token is gone |

**My take:** for a browser SPA, an `httpOnly` cookie is the safest home for the refresh token, paired with CSRF protection. For mobile and desktop apps, return it in the response body and store it in the platform's secure storage (Keychain, Keystore). **Avoid `localStorage`** for refresh tokens - it is the easiest target for an XSS attack. This sample returns the token in the response body to keep it client-agnostic, and the article explains the trade-off so you can pick what fits your app.

## Testing It

The sample seeds a default admin (`admin@codewithmukesh.com` / `Admin123!`), so you can test immediately with the Scalar UI at `/scalar/v1` or the included `requests.http`. Here is the flow I ran while writing this, with the real results:

- **Log in** - returns an access token (15 min) and a refresh token (7 days).
- **Call `/api/secured` with the access token** - `200 OK`.
- **Call `/api/auth/refresh` with the refresh token** - returns a new pair; the refresh token is rotated (the new one is different).
- **Reuse the old, now-revoked refresh token** - `401 Unauthorized`, and reuse detection revokes the whole family, so even the new token stops working.
- **Revoke a token, then try to refresh with it** - `401 Unauthorized`.
- **Send an unknown refresh token** - `401 Unauthorized`.

## Production Hardening

The sample is complete and correct, but for a real deployment I would add two things on top:

- **Hash the refresh tokens before storing them.** Right now the raw token sits in the database. If that database leaks, every token is usable. Store a hash instead (for example `SHA-256`), and when a token comes in, hash it and look up by the hash. The user holds the only copy of the raw value.
- **Cap the refresh token lifetime and clean up old rows.** Expired and revoked tokens pile up. A [background job](/blog/ihostedservice-vs-backgroundservice-dotnet/) that deletes them keeps the table small, and a hard maximum lifetime (even with rotation) limits how long a session can live.

## Troubleshooting

**Refresh always returns 401?** Make sure you are sending the exact refresh token string from the last response. Because rotation revokes the previous token on every call, an old token will never work twice.

**Reuse detection logs everyone out unexpectedly?** That usually means the client is sending the same refresh token twice - often a race condition where two requests refresh at the same time. Make sure the client serializes refresh calls and stores the newest token before retrying requests.

**`RNGCryptoServiceProvider` is marked obsolete?** That is expected on .NET 6 and later. Switch to `RandomNumberGenerator.GetBytes(64)`.

**Refresh token works but the new access token is rejected?** The access token validation is separate. Re-check the issuer, audience, and signing key in `TokenValidationParameters` against [Part 1](/blog/jwt-authentication-in-aspnet-core/) - they must match what you signed with.

**Can't revoke a token you just refreshed?** Remember rotation already revoked the previous token. You can only revoke the current active one.

## Key Takeaways

- Refresh tokens exist because a JWT cannot be revoked - keep access tokens short and let refresh tokens handle longevity.
- Generate refresh tokens with `RandomNumberGenerator.GetBytes`, not the obsolete `RNGCryptoServiceProvider`.
- **Rotate** the refresh token on every use so each one works only once.
- **Detect reuse**: if a revoked token reappears, revoke the user's whole token family - this is the part most guides miss.
- Store refresh tokens in an `httpOnly` cookie for web apps and secure storage for mobile; never `localStorage`.
- In production, hash stored tokens and clean up expired rows.

## Summary

You now have a complete refresh token system in ASP.NET Core .NET 10: short-lived JWT access tokens, long-lived refresh tokens with **rotation** and **reuse detection**, and a revoke endpoint for logout - all backed by code that builds and runs with zero database setup. This is the setup I would actually ship, not a toy demo.

The full source code is in the [GitHub repository](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/05-api-security/refresh-tokens-in-aspnet-core). This is Part 2 of my API security series - if you have not read [Part 1 on JWT authentication](/blog/jwt-authentication-in-aspnet-core/), start there, since this guide builds directly on it. It is also part of my [.NET Web API Zero to Hero course](/blog/aspnet-core-webapi-crud-with-entity-framework-core-full-course/), alongside [API key authentication](/blog/api-key-authentication-aspnet-core/), [global exception handling](/blog/global-exception-handling-in-aspnet-core/), and the [options pattern](/blog/options-pattern-in-aspnet-core/).

If you would rather not hand-roll any of this, [Identity API endpoints](/blog/identity-endpoints-aspnet-core/) give you register, login, and refresh out of the box - that article covers exactly when the trade-off is worth it.

With authentication handled, the next step is authorization - controlling what each user can do once they are logged in. I cover that across [role-based](/blog/role-based-authorization-in-aspnet-core/), [claims-based](/blog/claims-based-authorization-in-aspnet-core/), [policy-based](/blog/policy-based-authorization-in-aspnet-core/), and [permission-based authorization](/blog/permission-based-authorization-in-aspnet-core/) - the last of which explains why a revoked permission can keep working until the token expires, and what to do about it.

Happy Coding :)
