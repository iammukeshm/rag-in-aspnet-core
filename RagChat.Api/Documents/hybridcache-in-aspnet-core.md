# HybridCache in ASP.NET Core .NET 10 - Complete Guide

I wrote custom `IDistributedCache` extension methods - `GetOrSetAsync`, `TryGetValue`, `SetAsync` with generics - for every project that needed Redis caching. Dozens of lines of boilerplate that I copied from project to project. HybridCache made all of that code obsolete with a single `GetOrCreateAsync` call. But that is not even the best part. The best part is stampede protection. I ran 100 concurrent requests against a cold cache. With `IMemoryCache`, 100 database queries fired. With HybridCache, exactly 1.

In this guide, I will walk you through everything about HybridCache in ASP.NET Core with .NET 10 - from the L1/L2 architecture to the full API surface, tag-based invalidation, Redis as an L2 backend, a side-by-side migration from `IDistributedCache`, BenchmarkDotNet numbers, and a stampede protection demo with log proof. I will also share my opinions on when HybridCache is the right call and when you should stick with what you have.

Let's get into it.

> **TL;DR:** HybridCache is the recommended caching approach for new .NET 10 projects. It combines L1 in-memory speed (0.05us hits) with optional L2 Redis durability, built-in stampede protection (100 concurrent requests = 1 database query), and tag-based invalidation via `RemoveByTagAsync`. Two lines to set up: install `Microsoft.Extensions.Caching.Hybrid` and call `AddHybridCache()` in Program.cs. It is a drop-in improvement over both `IMemoryCache` and `IDistributedCache`.

## What Is HybridCache in ASP.NET Core?

**HybridCache is a .NET library (GA since .NET 9, stable in .NET 10) that provides a unified caching API combining L1 in-memory caching with optional L2 distributed caching, built-in stampede protection, and tag-based invalidation through a single `GetOrCreateAsync` method.** It ships in the [Microsoft.Extensions.Caching.Hybrid](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Hybrid) NuGet package and is the [recommended caching approach](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0) for new ASP.NET Core projects.

Here is how the L1/L2 architecture works:

1. **L1 (In-Memory)** - every application instance maintains its own in-process memory cache, just like `IMemoryCache`. Cache hits at this level are nanosecond-scale with zero serialization.
2. **L2 (Distributed)** - an optional external cache (Redis, SQL Server, or any `IDistributedCache` implementation). When L1 misses, HybridCache checks L2 before hitting the database. Data at this level is serialized and shared across all instances.
3. **Factory execution** - if both L1 and L2 miss, HybridCache executes your factory delegate (the database query), stores the result in both L1 and L2, and returns it. Critically, only one concurrent caller executes the factory for a given key. All other callers wait for the result.

The [.NET Blog GA announcement](https://devblogs.microsoft.com/dotnet/hybrid-cache-is-now-ga/) describes it as a "drop-in replacement for `IDistributedCache` and `IMemoryCache`." That is mostly accurate, with some nuance I will cover later.

What makes HybridCache different from manually combining `IMemoryCache` + `IDistributedCache` is that it handles the coordination between the two layers automatically. You do not write code to check L1, then check L2, then run the query, then populate both caches. One method call does all of that. And the stampede protection means you never have 100 concurrent requests all running the same expensive query on a cache miss.

## When Should You Use HybridCache in ASP.NET Core?

Here is the full comparison across all three caching approaches in ASP.NET Core:

| Criteria | In-Memory (`IMemoryCache`) | Distributed (`IDistributedCache`) | HybridCache |
|---|---|---|---|
| **Data scope** | Single server process | Shared across all instances | L1 per-process + L2 shared |
| **Network latency** | None (same process) | 1-5ms per call | L1: none, L2: 1-5ms |
| **Survives restart** | No | Yes | L2: yes |
| **Serialization** | None (stores references) | Required (JSON/binary) | L1: none, L2: required |
| **Stampede protection** | No (manual `SemaphoreSlim`) | No (manual) | Yes (built-in) |
| **Tag-based invalidation** | No | No (manual) | Yes (`RemoveByTagAsync`) |
| **`GetOrCreateAsync`** | Yes (but no stampede protection) | No (manual or custom extensions) | Yes (with stampede protection) |
| **L1 + L2 layering** | No | No | Yes (automatic) |
| **API complexity** | Low | High (byte[] arrays, manual serialization) | Low (type-safe generics) |
| **Best for** | Single-instance APIs, lookup data | Multi-instance, shared state, pub/sub | Any topology, new .NET 10 projects |
| **Setup complexity** | 1 line | Redis infrastructure + extensions | 2 lines + optional L2 config |
| **Minimum .NET version** | All | All | .NET 9+ |

### When NOT to Use HybridCache

HybridCache is not always the right answer. Here are the cases where I would skip it:

- **Single-instance API with no L2 need.** If you are running one pod, `IMemoryCache` with `GetOrCreateAsync` is simpler and has zero abstraction overhead. HybridCache adds a thin layer that you do not need.
- **Redis direct features.** If you need pub/sub, streams, sorted sets, Lua scripting, or any Redis-specific data structure, use `StackExchange.Redis` directly. HybridCache abstracts Redis as a key-value L2, so you lose access to advanced Redis capabilities.
- **Session or per-user state.** HybridCache is designed for read-heavy, shared data (product catalogs, config values, lookup tables). For per-user session data, `IDistributedCache` with Redis is a better fit because you rarely need L1 caching for user-specific data.

**My take:** For new .NET 10 projects, I default to HybridCache. Even without an L2 configured, it gives me stampede protection and tag-based invalidation that `IMemoryCache` does not have. The moment I add a second pod, I add Redis as L2 and the code does not change at all. That is the real win: the same API works for single-instance development and multi-instance production. For existing projects with well-tested `IDistributedCache` extensions, I do not rush to migrate. The extensions work fine. But any new service gets HybridCache from day one.

## How to Set Up HybridCache in .NET 10

Let me walk through setting up HybridCache in an ASP.NET Core .NET 10 Web API. I will use PostgreSQL with EF Core 10 for the database layer and Scalar for API documentation, same as the previous caching articles.

First, create a new .NET 10 Web API project and install the required packages:

```bash
dotnet add package Microsoft.Extensions.Caching.Hybrid --version 10.4.0
dotnet add package Microsoft.EntityFrameworkCore --version 10.0.0
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.0
```

The `Microsoft.Extensions.Caching.Hybrid` package is the only new addition compared to the [in-memory caching](/blog/in-memory-caching-in-aspnet-core/) and [Redis caching](/blog/distributed-caching-in-aspnet-core-with-redis/) setups. Check the [NuGet page](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Hybrid) for the latest version.

> Package versions shown are current as of April 2026. The `EXTEXP0018` experimental warning may be removed in a future .NET release - check whether the pragma is still needed for your version.

Now register HybridCache in `Program.cs`:

```csharp showLineNumbers
#pragma warning disable EXTEXP0018

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Expiration = TimeSpan.FromMinutes(30)
    };
    options.MaximumPayloadBytes = 1024 * 1024; // 1 MB max cache entry size
});
```

A few important things to note here:

**The `#pragma warning disable EXTEXP0018` is required.** HybridCache APIs are still marked as experimental in .NET 10 with the `[Experimental]` attribute. Without this pragma, you will get a compiler error. This does not mean the library is unstable - it has been GA since .NET 9 and is production-ready. The experimental flag means the API surface (method signatures, option names) could change in a future minor release without a major version bump. In practice, the core `GetOrCreateAsync` and `RemoveByTagAsync` APIs have been stable since the .NET 9 GA. Track the [dotnet/extensions repository](https://github.com/dotnet/extensions) for any breaking changes. You can also suppress this in your `.csproj` file with `<NoWarn>EXTEXP0018</NoWarn>` if you prefer a project-wide suppression.

**`DefaultEntryOptions`** sets the fallback expiration values for every cache entry that does not specify its own options. `LocalCacheExpiration` controls the L1 (in-memory) lifetime. `Expiration` controls the L2 (distributed) lifetime.

**`MaximumPayloadBytes`** caps the maximum size of a single cache entry. Entries exceeding this limit are silently skipped (not cached). I always set this to prevent accidentally caching a massive object graph that blows up memory.

That is it. Without any L2 configured, HybridCache works as a pure in-memory cache with stampede protection and tag-based invalidation. You get value from day one without Redis infrastructure.

## What Methods Does HybridCache Provide?

HybridCache has four methods. That is the entire API surface. Compare that to the dozens of extension methods I wrote for `IDistributedCache` in my [Redis caching article](/blog/distributed-caching-in-aspnet-core-with-redis/), and you see why this library exists.

### GetOrCreateAsync (The Workhorse)

This is the method you will use 90% of the time. It handles the entire cache-aside pattern in one call:

```csharp showLineNumbers
var product = await hybridCache.GetOrCreateAsync(
    $"product:{id}",                              // cache key
    async ct => await context.Products            // factory (runs on miss)
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == id, ct),
    new HybridCacheEntryOptions                   // optional per-entry options
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Expiration = TimeSpan.FromMinutes(30)
    },
    tags: ["products"],                           // tags for bulk invalidation
    cancellationToken: cancellationToken
);
```

Here is the full flow when you call `GetOrCreateAsync`:

1. Check L1 (in-memory). If hit, return immediately. No serialization, nanosecond-scale.
2. If L1 misses and L2 is configured, check L2 (Redis). If hit, deserialize the result, store it in L1, and return.
3. If both miss, acquire a lock for this cache key (stampede protection). Only one caller executes the factory delegate. All other concurrent callers for the same key wait for the result.
4. Store the result in both L1 and L2 (if configured). Return the value.

The factory delegate receives a `CancellationToken` parameter. Always use it instead of capturing the outer cancellation token, because HybridCache manages cancellation internally.

### SetAsync (Direct Set)

For cases where you want to cache a value without the factory pattern:

```csharp
await hybridCache.SetAsync(
    $"product:{product.Id}",
    product,
    new HybridCacheEntryOptions
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Expiration = TimeSpan.FromMinutes(30)
    },
    tags: ["products"],
    cancellationToken: cancellationToken
);
```

I use `SetAsync` when I want to pre-warm the cache after a create or update operation instead of just invalidating.

### RemoveAsync (Single Key)

Removes a specific cache entry by key from both L1 and L2:

```csharp
await hybridCache.RemoveAsync($"product:{id}", cancellationToken);
```

### RemoveByTagAsync (Bulk Invalidation)

This is the killer feature that neither `IMemoryCache` nor `IDistributedCache` provides. Remove all cache entries associated with a tag:

```csharp
await hybridCache.RemoveByTagAsync("products", cancellationToken);
```

This single call invalidates every entry that was created with the `"products"` tag, regardless of the individual cache key. I will demonstrate the power of this in the tag-based invalidation deep dive section.

### HybridCacheEntryOptions Breakdown

Understanding the options is critical for avoiding stale data issues:

```csharp
var options = new HybridCacheEntryOptions
{
    LocalCacheExpiration = TimeSpan.FromMinutes(5),  // L1 lifetime
    Expiration = TimeSpan.FromMinutes(30),           // L2 lifetime
    Flags = HybridCacheEntryFlags.None               // default behavior
};
```

**`LocalCacheExpiration`** - how long the entry lives in the L1 in-memory cache on each instance. After this expires, the next request checks L2 (Redis) instead of executing the factory. Keep this shorter than `Expiration` to ensure instances pick up L2 updates within a reasonable window.

**`Expiration`** - how long the entry lives in the L2 distributed cache. This is the "true" cache duration. When L2 expires, the factory executes again on the next request.

**Why two expiration values?** Consider a scenario with three API pods sharing a Redis L2. Pod 1 caches a product list. Pod 2 has the same data in its L1. An admin updates a product and invalidates the Redis key. If Pod 2's `LocalCacheExpiration` is 30 minutes, it serves stale data for up to 30 minutes. If it is 5 minutes, the staleness window is much smaller. The trade-off: shorter L1 expiration means more L2 lookups (network calls), but fresher data.

**My take:** I set `LocalCacheExpiration` to 5 minutes and `Expiration` to 30 minutes as my default. For data that changes rarely (permission sets, configuration), I push L1 to 15 minutes. For data that changes frequently (order counts, inventory), I drop L1 to 1-2 minutes. If you are using the [Options Pattern](/blog/options-pattern-in-aspnet-core/), you can bind these values from `appsettings.json` instead of hardcoding them.

**`Flags`** - controls special behaviors. `HybridCacheEntryFlags.DisableLocalCacheRead` skips the L1 check and always goes to L2. `DisableLocalCacheWrite` prevents storing in L1. These are useful for debugging or when you need immediate consistency at the cost of performance.

### Avoiding Closure Allocations with TState

For hot paths where you want to avoid the heap allocation from capturing variables in the factory lambda, use the `TState` overload of `GetOrCreateAsync`:

```csharp showLineNumbers
var product = await hybridCache.GetOrCreateAsync(
    $"product:{id}",
    id,                                                // state passed to factory
    static async (productId, ct) => await context.Products
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == productId, ct),
    cancellationToken: cancellationToken
);
```

The `static` keyword on the lambda prevents accidental variable capture. The state parameter (`id`) is passed directly to the factory without allocating a closure object. For most applications, this optimization is unnecessary - the standard closure-based approach is perfectly fine. I only reach for `TState` on endpoints handling 10,000+ RPM where every allocation matters.

### Custom Serialization

HybridCache uses `System.Text.Json` by default for L2 serialization. If you need binary serialization for better performance or have types with circular references, you can register a custom serializer by implementing `IHybridCacheSerializer<T>`:

```csharp
builder.Services.AddSingleton<IHybridCacheSerializer<Product>, ProductProtobufSerializer>();
```

This is useful when you are already using [MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp) or [MemoryPack](https://github.com/Cysharp/MemoryPack) across your project and want consistent serialization. For most applications, the default `System.Text.Json` serializer works well and requires no configuration.

## Building a Product CRUD API with HybridCache

Let me build a complete Product API that demonstrates every HybridCache feature. I will use the same Product model from the previous caching articles, with an added `Category` property for the tag-based invalidation demo.

### Product Model

```csharp showLineNumbers
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public decimal Price { get; set; }
    public string Category { get; set; } = default!;

    private Product() { }

    public Product(string name, string description, decimal price, string category)
    {
        Id = Guid.NewGuid();
        Name = name;
        Description = description;
        Price = price;
        Category = category;
    }
}

public record ProductCreationDto(string Name, string Description, decimal Price, string Category);
```

The `Category` property is the key addition. It enables tagging cache entries by category so I can invalidate "all Electronics products" without touching "all Clothing products."

I set up an `AppDbContext` with EF Core 10 targeting PostgreSQL and seeded 1,000 fake Product records across multiple categories. I will not walk through the EF Core setup here since I covered it in my [EF Core CRUD guide](/blog/aspnet-core-webapi-crud-with-entity-framework-core-full-course/). The connection string:

```json
"ConnectionStrings": {
  "Database": "Host=localhost;Database=hybridcaching;Username=postgres;Password=yourpassword;Include Error Detail=true"
}
```

> Replace these credentials with your own. In production, use [environment variables or user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) instead of hardcoding connection strings.

### Product Service with HybridCache

Here is the complete `ProductService`. `HybridCache`, `AppDbContext`, and `ILogger<ProductService>` are injected via the primary constructor:

```csharp showLineNumbers
public class ProductService(
    AppDbContext context,
    HybridCache cache,
    ILogger<ProductService> logger) : IProductService
{
    private const string AllProductsCacheKey = "products";

    public async Task<List<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Fetching data for key: {CacheKey}.", AllProductsCacheKey);

        var products = await cache.GetOrCreateAsync(
            AllProductsCacheKey,
            async ct =>
            {
                logger.LogInformation("Cache miss for key: {CacheKey}. Fetching from database.", AllProductsCacheKey);
                return await context.Products.AsNoTracking().ToListAsync(ct);
            },
            new HybridCacheEntryOptions
            {
                LocalCacheExpiration = TimeSpan.FromMinutes(5),
                Expiration = TimeSpan.FromMinutes(30)
            },
            tags: ["products"],
            cancellationToken: cancellationToken);

        return products ?? [];
    }

    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"product:{id}";
        logger.LogInformation("Fetching data for key: {CacheKey}.", cacheKey);

        var product = await cache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                logger.LogInformation("Cache miss for key: {CacheKey}. Fetching from database.", cacheKey);
                return await context.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == id, ct);
            },
            tags: ["products"],
            cancellationToken: cancellationToken);

        return product;
    }

    public async Task<List<Product>> GetByCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"products:category:{category}";
        logger.LogInformation("Fetching data for key: {CacheKey}.", cacheKey);

        var products = await cache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                logger.LogInformation("Cache miss for key: {CacheKey}. Fetching from database.", cacheKey);
                return await context.Products.AsNoTracking()
                    .Where(p => p.Category == category)
                    .ToListAsync(ct);
            },
            tags: ["products", $"category:{category}"],
            cancellationToken: cancellationToken);

        return products ?? [];
    }

    public async Task<Product> CreateAsync(ProductCreationDto request, CancellationToken cancellationToken = default)
    {
        var product = new Product(request.Name, request.Description, request.Price, request.Category);
        await context.Products.AddAsync(product, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Invalidating cache for tags: products, category:{Category}.", request.Category);
        await cache.RemoveByTagAsync("products", cancellationToken);

        return product;
    }

    public async Task<Product?> UpdateAsync(Guid id, ProductCreationDto request, CancellationToken cancellationToken = default)
    {
        var product = await context.Products.FindAsync([id], cancellationToken);
        if (product is null) return null;

        product.Name = request.Name;
        product.Description = request.Description;
        product.Price = request.Price;
        product.Category = request.Category;

        await context.SaveChangesAsync(cancellationToken);

        // Invalidate the individual product and all list caches
        logger.LogInformation("Invalidating cache for key: product:{ProductId} and tag: products.", id);
        await cache.RemoveAsync($"product:{id}", cancellationToken);
        await cache.RemoveByTagAsync("products", cancellationToken);

        return product;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await context.Products.FindAsync([id], cancellationToken);
        if (product is null) return false;

        context.Products.Remove(product);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Invalidating cache for key: product:{ProductId} and tag: products.", id);
        await cache.RemoveAsync($"product:{id}", cancellationToken);
        await cache.RemoveByTagAsync("products", cancellationToken);

        return true;
    }
}
```

Notice a few key patterns:

**Every `GetOrCreateAsync` call includes tags.** The `GetAllAsync` and `GetByIdAsync` methods use `["products"]`. The `GetByCategoryAsync` method uses both `["products", $"category:{category}"]`. This gives me fine-grained invalidation control.

**The factory delegate uses the `ct` parameter**, not the outer `cancellationToken`. HybridCache manages cancellation for the factory internally.

**No extension methods needed.** Compare this to the Redis article where I wrote `GetOrSetAsync`, `TryGetValue<T>`, and `SetAsync<T>` extension methods. HybridCache's `GetOrCreateAsync` replaces all of that boilerplate.

**`CreateAsync` uses `RemoveByTagAsync("products")`** to invalidate all product-related cache entries in one call. I could also invalidate just the specific category with `RemoveByTagAsync($"category:{request.Category}")`, but since a new product affects the "all products" list too, nuking the entire `"products"` tag is the safest approach.

**`UpdateAsync` combines `RemoveAsync` and `RemoveByTagAsync`.** It removes the individual product cache by key and then invalidates all list caches by tag. This dual approach ensures the updated product is not served stale from any cache layer, whether it was accessed by ID or as part of a list.

**`DeleteAsync` follows the same dual invalidation pattern.** After removing the product from the database, it removes the individual cache entry and invalidates all list tags. Always invalidate cache entries before returning from a write method, not after. If you invalidate after the response, a concurrent request could re-cache stale data between the database write and the invalidation.

### Minimal API Endpoints

Here is how the service is wired into [minimal API endpoints](/blog/minimal-apis-aspnet-core/):

```csharp
var products = app.MapGroup("/products").WithTags("Products");

products.MapGet("/", async (IProductService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetAllAsync(cancellationToken);
    return TypedResults.Ok(result);
});

products.MapGet("/{id:guid}", async (Guid id, IProductService service, CancellationToken cancellationToken) =>
{
    var product = await service.GetByIdAsync(id, cancellationToken);
    return product is not null
        ? TypedResults.Ok(product)
        : Results.NotFound();
});

products.MapGet("/category/{category}", async (string category, IProductService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetByCategoryAsync(category, cancellationToken);
    return TypedResults.Ok(result);
});

products.MapPost("/", async (ProductCreationDto request, IProductService service, CancellationToken cancellationToken) =>
{
    var product = await service.CreateAsync(request, cancellationToken);
    return TypedResults.Created($"/products/{product.Id}", product);
});

products.MapPut("/{id:guid}", async (Guid id, ProductCreationDto request, IProductService service, CancellationToken cancellationToken) =>
{
    var product = await service.UpdateAsync(id, request, cancellationToken);
    return product is not null
        ? TypedResults.Ok(product)
        : Results.NotFound();
});

products.MapDelete("/{id:guid}", async (Guid id, IProductService service, CancellationToken cancellationToken) =>
{
    var deleted = await service.DeleteAsync(id, cancellationToken);
    return deleted
        ? TypedResults.NoContent()
        : Results.NotFound();
});
```

Register the service as scoped in `Program.cs`:

```csharp
builder.Services.AddScoped<IProductService, ProductService>();
```

Using `TypedResults` instead of `Results` gives you strongly-typed responses and better OpenAPI documentation in Scalar. For more on this pattern, see my [Minimal APIs guide](/blog/minimal-apis-aspnet-core/).

## How to Add Redis as L2 Backend

HybridCache works as pure in-memory without any L2, but the real power comes when you add Redis. Here is the setup.

### Docker Compose for Redis

Create a `docker-compose.yml` in your project root:

```yaml
services:
  redis:
    image: redis:7.4
    container_name: redis
    ports:
      - "6379:6379"
    command: redis-server --requirepass yourpassword --appendonly yes
    volumes:
      - redis-data:/data

volumes:
  redis-data:
```

Start it with:

```bash
docker-compose up -d
```

For a deeper walkthrough of Docker for .NET development, see my [Docker guide](/blog/docker-guide-for-dotnet-developers/).

### Configuring Redis as L2

Install the Redis caching package:

```bash
dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis --version 10.0.0
```

Add the Redis connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Database": "Host=localhost;Database=hybridcaching;Username=postgres;Password=yourpassword;Include Error Detail=true",
    "Redis": "localhost:6379,password=yourpassword,abortConnect=false"
  }
}
```

> Replace these credentials with your own. In production, use environment variables or user secrets instead of hardcoding connection strings.

Now register both services in `Program.cs`:

```csharp showLineNumbers
#pragma warning disable EXTEXP0018

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    options.InstanceName = "codewithmukesh:";
});

builder.Services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Expiration = TimeSpan.FromMinutes(30)
    };
    options.MaximumPayloadBytes = 1024 * 1024;
});
```

That is it. **HybridCache automatically detects the `IDistributedCache` registration (from `AddStackExchangeRedisCache`) and uses it as L2.** You do not need to wire anything manually. The order of registration does not matter either.

Notice `abortConnect=false` in the Redis connection string. This tells StackExchange.Redis to not throw an exception if Redis is unavailable at startup. Instead, it retries the connection in the background. I always set this to `false` in production.

The `InstanceName` acts as a prefix for all cache keys in Redis. If you set it to `"codewithmukesh:"`, a key like `"products"` becomes `"codewithmukesh:products"` in Redis. This is useful when multiple applications share the same Redis instance.

With this configuration, HybridCache now operates in full L1 + L2 mode:

- **First request (cold start):** L1 miss, L2 miss, factory executes (database query), result stored in both L1 and L2.
- **Second request (same pod):** L1 hit. No network call, nanosecond response.
- **Request on a different pod:** L1 miss (different process), L2 hit (Redis). Deserialized and stored in that pod's L1.
- **After L1 expires (5 min):** L2 hit (Redis, still valid for 30 min). Re-populates L1.
- **After L2 expires (30 min):** Both miss. Factory executes again.

You can verify the L2 data in Redis Insight by browsing the `codewithmukesh:products` key.

## How Does Tag-Based Cache Invalidation Work?

This is the feature that no other caching approach in ASP.NET Core provides out of the box. With `IMemoryCache`, you track keys manually. With `IDistributedCache`, you either remove keys one by one or build a custom tag tracking system. HybridCache gives you `RemoveByTagAsync` for free.

### The Product Category Scenario

Consider this data:

- 200 products in the "Electronics" category
- 150 products in the "Clothing" category
- 100 products in the "Books" category

And these cached entries:

| Cache Key | Tags |
|---|---|
| `products` (all) | `["products"]` |
| `products:category:Electronics` | `["products", "category:Electronics"]` |
| `products:category:Clothing` | `["products", "category:Clothing"]` |
| `products:category:Books` | `["products", "category:Books"]` |
| `product:{id1}` (an Electronics item) | `["products"]` |
| `product:{id2}` (a Clothing item) | `["products"]` |

### Scenario 1: New Product Added

When a new Electronics product is created, I call:

```csharp
await cache.RemoveByTagAsync("products", cancellationToken);
```

This invalidates every entry tagged with `"products"`: the all-products list, every category list, and every individual product. The next request for any of these re-fetches from the database. This is the safest approach because a new product affects the all-products list and the category-specific list.

### Scenario 2: Category-Specific Invalidation

If I only want to invalidate Electronics entries (maybe a price update across the category), I can call:

```csharp
await cache.RemoveByTagAsync("category:Electronics", cancellationToken);
```

This invalidates only `products:category:Electronics` because that is the only entry with the `"category:Electronics"` tag. The "all products" list and other categories remain cached.

### Scenario 3: Combining Both

For a price update to a specific Electronics product, I might want to invalidate the individual product, the Electronics category list, and the all-products list:

```csharp
await cache.RemoveByTagAsync("products", cancellationToken);
```

Since all entries have the `"products"` tag, this handles everything. If I wanted to be more selective, I could call `RemoveByTagAsync` for just the specific tags, but in practice, I find the broad invalidation approach simpler and less error-prone.

**My take:** Tag-based invalidation is one of those features that seems like a nice-to-have until you build a real application with 20+ cache keys across different entity types. At that point, manually tracking which keys to remove on each write operation becomes a maintenance nightmare. Tags let you think in terms of "invalidate all product data" rather than "remove this key, and this key, and this key, and did I forget one?" I use broad tags (like `"products"`) for write operations and narrow tags (like `"category:Electronics"`) only when I need surgical precision.

## How to Migrate from IDistributedCache to HybridCache

If you have an existing project using `IDistributedCache` with custom extension methods (like the ones from my [Redis caching article](/blog/distributed-caching-in-aspnet-core-with-redis/)), here is a side-by-side migration guide.

### Before: IDistributedCache with Extensions

```csharp showLineNumbers
// DistributedCacheExtensions.cs - 50+ lines of custom code
public static class DistributedCacheExtensions
{
    public static Task SetAsync<T>(this IDistributedCache cache, string key, T value,
        DistributedCacheEntryOptions options, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        return cache.SetAsync(key, bytes, options, ct);
    }

    public static bool TryGetValue<T>(this IDistributedCache cache, string key, out T? value)
    {
        var val = cache.Get(key);
        value = default;
        if (val is null) return false;
        value = JsonSerializer.Deserialize<T>(val);
        return true;
    }

    public static async Task<T?> GetOrSetAsync<T>(this IDistributedCache cache, string key,
        Func<Task<T>> factory, DistributedCacheEntryOptions? options = null, CancellationToken ct = default)
    {
        if (cache.TryGetValue(key, out T? value) && value is not null) return value;
        value = await factory();
        if (value is not null) await cache.SetAsync(key, value, options ?? new(), ct);
        return value;
    }
}

// ProductService.cs
public class ProductService(AppDbContext context, IDistributedCache cache, ILogger<ProductService> logger) : IProductService
{
    public async Task<List<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var products = await cache.GetOrSetAsync(
            "products",
            async () => await context.Products.AsNoTracking().ToListAsync(cancellationToken),
            new DistributedCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(20))
                .SetSlidingExpiration(TimeSpan.FromMinutes(2)),
            cancellationToken);
        return products ?? [];
    }

    public async Task<Product> CreateAsync(ProductCreationDto request, CancellationToken cancellationToken = default)
    {
        var product = new Product(request.Name, request.Description, request.Price);
        await context.Products.AddAsync(product, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await cache.RemoveAsync("products", cancellationToken);
        return product;
    }
}
```

### After: HybridCache

```csharp showLineNumbers
// No extension methods file needed. Delete DistributedCacheExtensions.cs entirely.

// ProductService.cs
public class ProductService(AppDbContext context, HybridCache cache, ILogger<ProductService> logger) : IProductService
{
    public async Task<List<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var products = await cache.GetOrCreateAsync(
            "products",
            async ct => await context.Products.AsNoTracking().ToListAsync(ct),
            new HybridCacheEntryOptions
            {
                LocalCacheExpiration = TimeSpan.FromMinutes(5),
                Expiration = TimeSpan.FromMinutes(20)
            },
            tags: ["products"],
            cancellationToken: cancellationToken);
        return products ?? [];
    }

    public async Task<Product> CreateAsync(ProductCreationDto request, CancellationToken cancellationToken = default)
    {
        var product = new Product(request.Name, request.Description, request.Price, request.Category);
        await context.Products.AddAsync(product, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await cache.RemoveByTagAsync("products", cancellationToken);
        return product;
    }
}
```

### What Changes

1. **Delete the extension methods file.** `GetOrSetAsync`, `TryGetValue<T>`, and `SetAsync<T>` are all replaced by `HybridCache.GetOrCreateAsync` and `HybridCache.SetAsync`.
2. **Replace `IDistributedCache` injection with `HybridCache`.** The DI registration changes from `AddStackExchangeRedisCache()` only to `AddStackExchangeRedisCache()` + `AddHybridCache()`.
3. **Replace `GetOrSetAsync` with `GetOrCreateAsync`.** The API is almost identical, but the factory delegate now receives a `CancellationToken` parameter.
4. **Replace `cache.RemoveAsync` with `cache.RemoveByTagAsync`.** You get bulk invalidation for free.
5. **Add the `#pragma warning disable EXTEXP0018`** at the top of any file that calls HybridCache methods directly, or suppress it project-wide.

### What Stays the Same

- **Same cache key patterns.** `"products"`, `"product:{id}"` - your key naming does not change.
- **Same expiration strategy.** You still set absolute expiration. The difference is HybridCache adds a separate `LocalCacheExpiration` for L1.
- **Same Redis infrastructure.** The `AddStackExchangeRedisCache()` registration stays. HybridCache uses it as L2.

**My take on migration timing:** If your `IDistributedCache` setup is working well in production, there is no urgency to migrate. The custom extensions are battle-tested in your codebase. I would migrate when you are doing a major refactor anyway, or when you add a new service that would benefit from stampede protection. The migration is low-risk because the behavior is nearly identical. Just make sure to test your invalidation patterns since tag-based invalidation has slightly different semantics than individual key removal.

## How Does HybridCache Prevent Cache Stampedes?

This is the section I am most excited about. Every article about HybridCache mentions stampede protection. Nobody shows it.

### What Is a Cache Stampede?

Imagine your product catalog cache expires. At that exact moment, 100 requests hit the `/products` endpoint simultaneously. With `IMemoryCache`, all 100 requests see a cache miss and all 100 execute the database query. That is 100 identical queries hitting PostgreSQL at the same time. If the query takes 500ms and the database connection pool has 20 connections, the remaining 80 requests queue up, timeouts start cascading, and your API returns 500 errors. This is a cache stampede, also called a thundering herd.

### IMemoryCache Behavior: No Protection

Here is a minimal reproduction. I will fire 100 concurrent requests at a cold cache using `IMemoryCache`:

```csharp showLineNumbers
// StampedeTestService using IMemoryCache
public class MemoryCacheStampedeService(AppDbContext context, IMemoryCache cache, ILogger<MemoryCacheStampedeService> logger)
{
    private static int _factoryExecutionCount = 0;

    public async Task<List<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var products = await cache.GetOrCreateAsync("products", async entry =>
        {
            var count = Interlocked.Increment(ref _factoryExecutionCount);
            logger.LogWarning("IMemoryCache factory executing. Execution #{Count}", count);

            entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
            await Task.Delay(200, cancellationToken); // Simulate slow DB query
            return await context.Products.AsNoTracking().ToListAsync(cancellationToken);
        });

        return products ?? [];
    }

    public static int GetExecutionCount() => _factoryExecutionCount;
    public static void Reset() => _factoryExecutionCount = 0;
}
```

Now fire 100 concurrent requests:

```csharp showLineNumbers
// Stampede test endpoint
app.MapGet("/stampede/memory", async (MemoryCacheStampedeService service) =>
{
    MemoryCacheStampedeService.Reset();

    var tasks = Enumerable.Range(0, 100)
        .Select(_ => service.GetAllAsync())
        .ToArray();

    await Task.WhenAll(tasks);

    return TypedResults.Ok(new
    {
        FactoryExecutions = MemoryCacheStampedeService.GetExecutionCount(),
        Message = "Check the logs for factory execution count"
    });
});
```

**Expected output:**

```
warn: MemoryCacheStampedeService - IMemoryCache factory executing. Execution #1
warn: MemoryCacheStampedeService - IMemoryCache factory executing. Execution #2
warn: MemoryCacheStampedeService - IMemoryCache factory executing. Execution #3
...
warn: MemoryCacheStampedeService - IMemoryCache factory executing. Execution #97
warn: MemoryCacheStampedeService - IMemoryCache factory executing. Execution #98
```

Roughly 90-100 factory executions. 90-100 database queries. `IMemoryCache.GetOrCreateAsync` does not hold a lock for the same key. Every concurrent caller sees the cache as empty and runs the factory.

### HybridCache Behavior: Built-In Protection

Now the same test with HybridCache:

```csharp showLineNumbers
// StampedeTestService using HybridCache
public class HybridCacheStampedeService(AppDbContext context, HybridCache cache, ILogger<HybridCacheStampedeService> logger)
{
    private static int _factoryExecutionCount = 0;

    public async Task<List<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var products = await cache.GetOrCreateAsync(
            "stampede-test-products",
            async ct =>
            {
                var count = Interlocked.Increment(ref _factoryExecutionCount);
                logger.LogWarning("HybridCache factory executing. Execution #{Count}", count);

                await Task.Delay(200, ct); // Simulate slow DB query
                return await context.Products.AsNoTracking().ToListAsync(ct);
            },
            tags: ["stampede-test"],
            cancellationToken: cancellationToken);

        return products ?? [];
    }

    public static int GetExecutionCount() => _factoryExecutionCount;
    public static void Reset() => _factoryExecutionCount = 0;
}
```

```csharp showLineNumbers
// Stampede test endpoint
app.MapGet("/stampede/hybrid", async (HybridCacheStampedeService service, HybridCache cache) =>
{
    HybridCacheStampedeService.Reset();
    await cache.RemoveByTagAsync("stampede-test");

    var tasks = Enumerable.Range(0, 100)
        .Select(_ => service.GetAllAsync())
        .ToArray();

    await Task.WhenAll(tasks);

    return TypedResults.Ok(new
    {
        FactoryExecutions = HybridCacheStampedeService.GetExecutionCount(),
        Message = "Check the logs for factory execution count"
    });
});
```

**Expected output:**

```
warn: HybridCacheStampedeService - HybridCache factory executing. Execution #1
```

One. Exactly one factory execution. One database query. The other 99 requests waited for the first one to complete and received the same result. HybridCache uses a `SemaphoreSlim`-based locking mechanism internally, keyed by the cache key. When the first caller acquires the lock and starts executing the factory, all subsequent callers for the same key await the lock instead of running the factory again.

**My take:** This alone justifies switching to HybridCache. I have seen cache stampedes take down production databases during peak traffic. On one project, a Redis key with a 15-minute TTL expired during a traffic spike of around 2,000 RPM. Without stampede protection, 200+ concurrent queries hit PostgreSQL simultaneously, exhausting the connection pool (max 100) and causing cascading timeouts for 45 seconds until the pool recovered. The typical fix is a manual `SemaphoreSlim` wrapper around your caching code, which is error-prone and easy to forget. HybridCache makes stampede protection the default behavior. You do not even have to think about it.

## How Fast Is HybridCache Compared to IMemoryCache and Redis?

**HybridCache L1 cache hits take approximately 0.05 microseconds - only 30 nanoseconds slower than raw `IMemoryCache` (0.02us) but 24,000x faster than Redis `IDistributedCache` calls (1,200us).** The abstraction overhead is invisible at the API level and buys you stampede protection, tag-based invalidation, and optional L2 support. Here are the full BenchmarkDotNet results from my test environment.

I set up a [BenchmarkDotNet](https://benchmarkdotnet.org/) project (included in the [companion repo](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/04-performance-and-caching/hybridcache-in-aspnet-core)) to measure the raw performance of each caching layer. This extends the benchmarks from my [in-memory caching](/blog/in-memory-caching-in-aspnet-core/) and [Redis caching](/blog/distributed-caching-in-aspnet-core-with-redis/) articles with HybridCache numbers.

```csharp showLineNumbers
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class HybridCacheBenchmarks
{
    private AppDbContext _dbContext = null!;
    private IMemoryCache _memoryCache = null!;
    private IDistributedCache _redisCache = null!;
    private HybridCache _hybridCache = null!;
    private Guid _productId;

    [GlobalSetup]
    public void Setup()
    {
        // Configure real PostgreSQL + Redis connections
        // Register HybridCache with Redis as L2
        // Pre-warm all caches with the same product data
        // Full setup in the repo's HybridCacheBenchmarks.cs
    }

    [Benchmark(Baseline = true)]
    public async Task<Product?> SingleProduct_DatabaseFetch()
    {
        return await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == _productId);
    }

    [Benchmark]
    public Product? SingleProduct_MemoryCacheHit()
    {
        _memoryCache.TryGetValue($"product:{_productId}", out Product? product);
        return product;
    }

    [Benchmark]
    public async Task<Product?> SingleProduct_RedisCacheHit()
    {
        var bytes = await _redisCache.GetAsync($"product:{_productId}");
        return bytes is not null
            ? JsonSerializer.Deserialize<Product>(bytes)
            : null;
    }

    [Benchmark]
    public async Task<Product?> SingleProduct_HybridCache_L1Hit()
    {
        return await _hybridCache.GetOrCreateAsync(
            $"product:{_productId}",
            async ct => await _dbContext.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == _productId, ct));
    }

    [Benchmark]
    public async Task<List<Product>> AllProducts_DatabaseFetch()
    {
        return await _dbContext.Products
            .AsNoTracking()
            .Take(1000)
            .ToListAsync();
    }

    [Benchmark]
    public List<Product>? AllProducts_MemoryCacheHit()
    {
        _memoryCache.TryGetValue("products", out List<Product>? products);
        return products;
    }

    [Benchmark]
    public async Task<List<Product>?> AllProducts_RedisCacheHit()
    {
        var bytes = await _redisCache.GetAsync("products");
        return bytes is not null
            ? JsonSerializer.Deserialize<List<Product>>(bytes)
            : null;
    }

    [Benchmark]
    public async Task<List<Product>> AllProducts_HybridCache_L1Hit()
    {
        return await _hybridCache.GetOrCreateAsync(
            "products",
            async ct => await _dbContext.Products.AsNoTracking()
                .Take(1000)
                .ToListAsync(ct)) ?? [];
    }
}
```

### Benchmark Results

| Method | Mean | Allocated |
|---|---|---|
| SingleProduct_DatabaseFetch | ~500 us | ~8 KB |
| SingleProduct_MemoryCacheHit | ~0.02 us | 0 B |
| SingleProduct_RedisCacheHit | ~1,200 us | ~4 KB |
| SingleProduct_HybridCache_L1Hit | ~0.05 us | ~200 B |
| AllProducts_DatabaseFetch (1000) | ~12,000 us | ~650 KB |
| AllProducts_MemoryCacheHit (1000) | ~0.02 us | 0 B |
| AllProducts_RedisCacheHit (1000) | ~3,500 us | ~420 KB |
| AllProducts_HybridCache_L1Hit (1000) | ~0.08 us | ~400 B |

> **Test environment:** .NET 10.0.0, BenchmarkDotNet v0.14.x, PostgreSQL 17 and Redis 7.4 running in Docker Desktop on Windows 11. Results are mean values across 10 iterations with 3 warmup rounds. Your results will vary based on hardware and network topology. Full benchmark source code is in the [companion repo](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/04-performance-and-caching/hybridcache-in-aspnet-core).

### Analysis

**HybridCache L1 is slightly slower than raw `IMemoryCache`.** The single-product L1 hit is ~0.05us versus ~0.02us for `IMemoryCache`. The 1000-item list is ~0.08us versus ~0.02us. The difference comes from HybridCache's abstraction layer: it checks whether the entry is still valid, runs through the options evaluation, and wraps the result. It also allocates ~200-400 bytes versus 0 bytes because HybridCache creates internal state objects for each call.

**The difference is negligible in practice.** That is 0.03 microseconds of overhead. That is 30 nanoseconds. Your HTTP pipeline adds 50-200 microseconds of overhead per request. JSON serialization adds more. The HybridCache abstraction cost is invisible at the API level.

**The real comparison is HybridCache L1 vs Redis (L2).** A HybridCache L1 hit at ~0.05us versus a raw Redis call at ~1,200us is a 24,000x improvement. That is the whole point of the L1 layer: you get Redis durability for cross-instance consistency, but 99% of requests serve from L1 at in-memory speed.

**My take:** If you are choosing between `IMemoryCache` and HybridCache purely on raw L1 speed, `IMemoryCache` wins by a trivial margin. But HybridCache gives you stampede protection, tag-based invalidation, and an optional L2 backend that `IMemoryCache` will never have. The 30-nanosecond overhead is the cheapest insurance policy in software engineering.

For more on optimizing the database queries that happen on cache misses, check out my articles on [compiled queries in EF Core](/blog/ef-core-performance-mistakes/) and [tracking vs no-tracking queries](/blog/tracking-vs-no-tracking-queries-efcore/).

## What Are the Common HybridCache Pitfalls and How to Fix Them?

After working with HybridCache across multiple .NET 10 projects, here are the pitfalls I see most often:

| Pitfall | Cause | Fix |
|---|---|---|
| Pods serve stale data | `LocalCacheExpiration` too long relative to `Expiration` | Keep L1:L2 ratio at 1:6 (5 min L1, 30 min L2) |
| `JsonException` on L2 reads | Model changed after deploy, Redis has old format | Version cache keys (`"v2:products"`) or invalidate before deploy |
| Entries silently not cached | Serialized size exceeds `MaximumPayloadBytes` | Increase limit or cache DTOs instead of full entity graphs |
| Cache hit rate drops to zero on writes | `RemoveByTagAsync` with overly broad tags | Use granular tags (`"products:list"`, `"category:Electronics"`) |
| Factory runs despite L2 having data | Redis timeout or deserialization failure | Check Redis latency, verify model compatibility |
| API keeps working when Redis is down | L1 serves independently, L2 fails gracefully | This is a feature, not a bug - HybridCache degrades gracefully |

### L1/L2 Expiration Mismatch

**Problem:** Your Redis L2 cache gets updated (via another pod or direct Redis command), but some pods keep serving stale data for longer than expected.

**Cause:** `LocalCacheExpiration` (L1) is set too long relative to `Expiration` (L2). If L1 is 30 minutes and L2 is 30 minutes, a pod that cached data at minute 0 will serve stale L1 data for the full 30 minutes even if another pod invalidated the L2 entry at minute 5.

**Solution:** Keep `LocalCacheExpiration` significantly shorter than `Expiration`. My default ratio is 1:6 (5 minutes L1, 30 minutes L2). This limits the staleness window to the L1 expiration time.

### Serialization Failures with Redis L2

**Problem:** `JsonException` when HybridCache tries to read from L2 after you deployed a new version that changed a model property.

**Cause:** Redis L2 has the old serialized format. Deserialization fails because the JSON shape no longer matches the C# type. This is the same issue from raw `IDistributedCache`, and HybridCache does not magically solve it.

**Solution:** Use a versioned cache key pattern (e.g., `"v2:products"`) when making breaking model changes, or configure lenient serialization with `PropertyNameCaseInsensitive = true`. For a graceful approach, invalidate all affected tags before deploying the new version.

### MaximumPayloadBytes Rejecting Entries Silently

**Problem:** Certain cache entries never get cached. No errors in logs, no exceptions, just repeated cache misses for the same key.

**Cause:** The serialized size of the entry exceeds `MaximumPayloadBytes`. HybridCache silently skips entries that are too large instead of throwing an exception. This is by design to prevent large entries from degrading cache performance.

**Solution:** Increase `MaximumPayloadBytes` or reduce the data you are caching. Use DTOs instead of full entity graphs. You can add logging in your factory delegate to track when it executes repeatedly for the same key, which helps identify entries that are being silently rejected.

### Tag Invalidation Scope Too Broad

**Problem:** Invalidating the `"products"` tag nukes the all-products list, every category list, and every individual product entry. Your cache hit rate drops to near zero after every write operation.

**Cause:** Every product-related entry was tagged with `"products"`. A single `RemoveByTagAsync("products")` wipes everything.

**Solution:** Use more granular tags. Instead of tagging everything with `"products"`, use specific tags like `"products:list"` for the all-products cache and `"category:Electronics"` for category-specific caches. Only invalidate the most specific tag that applies to the change.

### Missing Redis Fallback (Actually a Feature)

**Problem:** Redis goes down, and you expect the API to fail. But it keeps working with slightly stale data.

**Cause:** HybridCache's L1 layer is process-local and independent of Redis. When Redis is unreachable, L1 hits still work. L2 misses fail gracefully, and the factory executes to repopulate data.

**This is actually a feature, not a bug.** HybridCache degrades gracefully. When Redis comes back, L2 starts serving again. The only risk is that different pods might have slightly different L1 data during the Redis outage. For most read-heavy workloads, this is perfectly acceptable.

### Factory Executing on L1 Miss Even When L2 Has Data

**Problem:** You see factory executions in your logs even though Redis has the data. You expected HybridCache to check L2 before running the factory.

**Cause:** This is not a bug. HybridCache does check L2 on L1 miss. But if the L2 lookup fails (timeout, deserialization error, Redis unreachable), it falls back to the factory. Check your Redis connectivity and serialization configuration.

**Solution:** Monitor Redis latency and connection health. If L2 lookups are timing out, increase the Redis connection timeout. If deserialization is failing, check for model changes. For error handling patterns, see my [global exception handling guide](/blog/global-exception-handling-in-aspnet-core/).

## Key Takeaways

1. **HybridCache combines L1 in-memory speed with L2 distributed durability through a single `GetOrCreateAsync` API.** No custom extension methods, no manual cache-aside logic, no boilerplate.
2. **Stampede protection is built-in and automatic.** 100 concurrent requests on a cache miss result in exactly 1 database query. This alone justifies using HybridCache over `IMemoryCache` for any endpoint with concurrent traffic.
3. **Tag-based invalidation with `RemoveByTagAsync` replaces manual key tracking.** Invalidate all product cache entries with one call instead of removing individual keys and hoping you did not miss one.
4. **HybridCache L1 hits are ~0.05us, only 0.03us slower than raw `IMemoryCache`.** The 30-nanosecond abstraction cost is invisible at the API level and buys you stampede protection, tag invalidation, and L2 support.
5. **Migration from `IDistributedCache` is straightforward.** Replace the injection, swap `GetOrSetAsync` for `GetOrCreateAsync`, delete the extension methods file, and add tags to your cache entries.
6. **Keep `LocalCacheExpiration` shorter than `Expiration`.** My default ratio is 1:6 (5 minutes L1, 30 minutes L2) to limit the staleness window across multiple instances.
7. **`#pragma warning disable EXTEXP0018` is required in .NET 10** because HybridCache APIs are marked as experimental. This does not mean the library is unstable. It is GA and production-ready since .NET 9.

## Summary

HybridCache is the caching library that ASP.NET Core should have had from the start. It eliminates the boilerplate of `IDistributedCache` extensions, solves the stampede problem that `IMemoryCache` ignores, and gives you L1 + L2 layering without writing the plumbing yourself. For new .NET 10 projects, it is my default.

The complete source code - including the BenchmarkDotNet project, stampede protection demo, and Docker compose file - is available in the [companion repository](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/04-performance-and-caching/hybridcache-in-aspnet-core).

If you found this helpful, share it with your colleagues. Happy Coding :)
