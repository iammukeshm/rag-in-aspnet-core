# In-Memory Caching in ASP.NET Core .NET 10 - Complete Guide

I had a product catalog endpoint that took 800ms per request. One line of code brought it down to 12ms. That line was `builder.Services.AddMemoryCache()`. Caching is one of those things that sounds simple but has real nuance once you start using it in production. When should you invalidate? How do you prevent stampedes? When should you use in-memory vs Redis vs HybridCache?

In this guide, I will walk you through everything about in-memory caching in ASP.NET Core with .NET 10 - from basic setup to cache invalidation strategies, `GetOrCreateAsync` patterns, BenchmarkDotNet numbers, common pitfalls, and a decision matrix for choosing the right caching approach. I will also share my opinions on when in-memory caching is the right call and when you should reach for something else.

Let's get into it.

## What Is Caching?

**Caching is the technique of storing frequently accessed data in temporary storage so future requests can be served faster without hitting the original data source.** Instead of querying your database every time a client asks for the same data, you store the result in a fast-access layer and serve it from there.

Here is a simplified example. Client #1 requests a list of products - it takes 800ms to fetch from PostgreSQL. The result gets copied to a temporary cache. When Client #2 requests the same data seconds later, it comes back from the cache in under 5ms. No database round-trip, no network latency to the DB server, no query compilation overhead.

You might be wondering: what happens if the data changes? Will the application serve outdated responses? No. There are strategies to refresh the cache and set expiration times to ensure accuracy. I will cover those in detail later.

One critical principle: **your application should never treat cached data as the source of truth.** If the cache is empty or expired, the app falls back to the original data source. Caching is an optimization layer, not a data store.

## Caching Types in ASP.NET Core .NET 10

ASP.NET Core supports three main caching approaches (documented in [Microsoft's caching overview](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/overview)):

1. **In-Memory Caching** - data cached in the server's process memory via `IMemoryCache`. Fastest option, zero network overhead, but scoped to a single server instance.
2. **Distributed Caching** - data stored externally (Redis, SQL Server, NCache) via `IDistributedCache`. Shared across multiple application instances, survives restarts, but adds network latency.
3. **HybridCache** - GA since .NET 9, stable in .NET 10. Combines L1 in-memory cache with an optional L2 distributed backend. Built-in stampede protection and [tag-based invalidation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid). The modern default for new projects.

Here is how the **Cache-Aside Pattern** works - the most common pattern for read-heavy applications:

The application checks the cache first. On a cache miss, it fetches from the database, stores the result in the cache, and returns it. On a cache hit, the cached data is returned directly without touching the database.

I have covered distributed caching with Redis in a [separate deep dive](/blog/distributed-caching-in-aspnet-core-with-redis/). In this article, I will focus entirely on in-memory caching with `IMemoryCache`.

## What Is In-Memory Caching in ASP.NET Core?

**In-memory caching in ASP.NET Core stores data in the web server's RAM using `IMemoryCache`, making it the fastest caching option because there is zero network overhead.** The data lives in the same process as your application, so access times are measured in nanoseconds rather than milliseconds.

Under the hood, `IMemoryCache` is backed by a `ConcurrentDictionary`. It is thread-safe for individual get and set operations, stores object references directly (no serialization), and is scoped to the application process. This means:

- Data is lost when the application restarts
- Each server instance has its own independent cache
- No serialization overhead - cached objects are the same .NET references in memory

For the official reference, see [Microsoft's in-memory caching documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory).

## When to Use In-Memory Caching - Decision Matrix

This is the question I get asked most: "Should I use in-memory, Redis, or HybridCache?" Here is my decision matrix:

| Criteria | In-Memory (`IMemoryCache`) | Distributed (Redis) | HybridCache |
|---|---|---|---|
| **Data scope** | Single server process | Shared across all instances | L1 per-process + L2 shared |
| **Network latency** | None (same process) | 1-5ms per call | L1: none, L2: 1-5ms |
| **Survives restart** | No | Yes | L2: yes |
| **Stampede protection** | No (manual `SemaphoreSlim`) | No (manual) | Yes (built-in) |
| **Tag-based invalidation** | No | No (manual) | Yes (`RemoveByTagAsync`) |
| **Best for** | Single-instance APIs, lookup data | Multi-instance, shared state | Any topology, new .NET 10 projects |
| **Setup complexity** | 1 line | Redis infrastructure needed | 2 lines + optional L2 config |
| **Minimum .NET version** | All | All | .NET 9+ |

**My take:** If I am building a single-instance API with lookup data that fits in RAM - configuration values, category lists, permission sets - I reach for `IMemoryCache` every time. It is the fastest option with the least complexity. The moment I need shared state across multiple pods or a [Kubernetes deployment](/blog/docker-guide-for-dotnet-developers/), I move to Redis. And for new .NET 10 projects where I want both the speed of L1 and the durability of L2 without writing that plumbing myself, I use HybridCache.

The mistake I see most developers make is reaching for Redis when a single-server `IMemoryCache` would do the job. Don't add infrastructure you don't need yet.

## Pros and Cons of In-Memory Caching

### Pros

1. **Fastest caching option** - no network hops, no serialization. Cache hits are nanosecond-scale operations.
2. **Zero infrastructure** - no Redis server, no external dependencies. Just `AddMemoryCache()` and you are done.
3. **Stores object references** - unlike distributed caching, there is no serialization/deserialization overhead. The cached object is the exact same reference in memory.
4. **Best for single-instance APIs** - if you are running one server instance, in-memory caching gives you the most performance per line of code.

### Cons

1. **Not shared across instances** - each application instance has its own cache. In a load-balanced setup, Client A might get cached data while Client B gets a cache miss for the same key.
2. **Lost on restart** - application restarts, deployments, or crashes wipe the cache entirely.
3. **Memory pressure** - without proper size limits, the cache can grow unbounded and eat into your server's available RAM. I cover how to prevent this in the troubleshooting section.
4. **No built-in stampede protection** - if 100 requests hit a cache miss simultaneously, all 100 will query the database. HybridCache solves this automatically.

In my experience, the scalability concern is overstated for most teams. If your API runs on a single instance (which covers a surprisingly large number of production workloads), in-memory caching is the right default.

## Getting Started with .NET 10

Let me walk through setting up in-memory caching in an ASP.NET Core .NET 10 Web API. I will use PostgreSQL with EF Core 10 for the database layer and Scalar for API documentation.

First, create a new .NET 10 Web API project and install the required packages:

```bash
dotnet add package Microsoft.EntityFrameworkCore --version 10.0.0
dotnet add package Microsoft.EntityFrameworkCore.Tools --version 10.0.0
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.0
```

To enable in-memory caching, add this single line to `Program.cs`:

```csharp
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 10_000; // Limit total cache size to prevent unbounded growth
});
```

This registers a non-distributed, in-memory implementation of `IMemoryCache` into the [dependency injection container](/blog/dependency-injection-in-aspnet-core-explained/). The `SizeLimit` property caps how many "size units" the cache can hold. I always set this in production to prevent memory issues.

> If you are new to minimal APIs, I recommend reading my [Minimal APIs in ASP.NET Core](/blog/minimal-apis-aspnet-core/) guide before continuing.

That's it. Your application now supports in-memory caching. Let me show you how to use it.

## Setting Up the Product Model

I will build a simple Product CRUD API to demonstrate caching. Here is the model:

```csharp
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public decimal Price { get; set; }

    private Product() { }

    public Product(string name, string description, decimal price)
    {
        Id = Guid.NewGuid();
        Name = name;
        Description = description;
        Price = price;
    }
}

public record ProductCreationDto(string Name, string Description, decimal Price);
```

I also set up an `AppDbContext` with EF Core 10 targeting PostgreSQL. I won't walk through the EF Core setup here since I have covered that thoroughly in my [EF Core CRUD guide](/blog/aspnet-core-webapi-crud-with-entity-framework-core-full-course/). The connection string looks like this:

```json
"ConnectionStrings": {
  "Database": "Host=localhost;Database=inmemorycaching;Username=postgres;Password=yourpassword;Include Error Detail=true"
}
```

> Replace these credentials with your own. In production, use [environment variables or user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) instead of hardcoding connection strings.

I seeded 1,000 fake Product records using a SQL script (included in the repo's `Scripts` folder) to have realistic data for benchmarking.

## Implementing Caching with IMemoryCache

Here is the `ProductService` class that demonstrates two caching patterns. I have injected `AppDbContext`, `IMemoryCache`, and `ILogger<ProductService>` via the primary constructor.

### Pattern 1: GetOrCreateAsync (Recommended)

The cleanest approach for most caching scenarios. It handles the cache-miss-then-populate logic in a single call:

```csharp showLineNumbers
public async Task<List<Product>> GetAllAsync(CancellationToken cancellationToken = default)
{
    var products = await cache.GetOrCreateAsync(AllProductsCacheKey, async entry =>
    {
        entry.SetSlidingExpiration(TimeSpan.FromSeconds(30))
             .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
             .SetPriority(CacheItemPriority.High)
             .SetSize(2048);

        logger.LogInformation("Cache miss for key: {CacheKey}. Fetching from database.", AllProductsCacheKey);
        return await context.Products.AsNoTracking().ToListAsync(cancellationToken);
    });

    return products ?? [];
}
```

`GetOrCreateAsync` checks the cache first. If the key exists, it returns the cached value. If not, it executes the factory delegate, stores the result, and returns it. One method call, no boilerplate.

### Pattern 2: Manual TryGetValue + Set

For scenarios where you need more control - different cache options based on the data, conditional caching, or post-eviction callbacks:

```csharp showLineNumbers
public async Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
{
    var cacheKey = $"product:{id}";

    if (cache.TryGetValue(cacheKey, out Product? product))
    {
        logger.LogInformation("Cache hit for key: {CacheKey}.", cacheKey);
        return product;
    }

    logger.LogInformation("Cache miss for key: {CacheKey}. Fetching from database.", cacheKey);
    product = await context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    if (product is not null)
    {
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(30))
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
            .SetPriority(CacheItemPriority.Normal)
            .SetSize(1)
            .RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                logger.LogInformation("Cache entry evicted. Key: {CacheKey}, Reason: {Reason}.", key, reason);
            });

        cache.Set(cacheKey, product, cacheOptions);
    }

    return product;
}
```

Notice the `RegisterPostEvictionCallback`. This fires whenever a cache entry is removed - whether by expiration, manual removal, or memory pressure. It is useful for [structured logging](/blog/structured-logging-with-serilog-in-aspnet-core/) and monitoring cache behavior in production.

**My take:** Use `GetOrCreateAsync` for 90% of your caching needs. It is cleaner and less error-prone. Only drop down to manual `TryGetValue` + `Set` when you need post-eviction callbacks, conditional caching logic, or different options per entry.

## Minimal API Endpoints

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

products.MapPost("/", async (ProductCreationDto request, IProductService service, CancellationToken cancellationToken) =>
{
    var product = await service.CreateAsync(request, cancellationToken);
    return TypedResults.Created($"/products/{product.Id}", product);
});
```

Register the service as scoped in `Program.cs`:

```csharp
builder.Services.AddScoped<IProductService, ProductService>();
```

## Cache Entry Options in ASP.NET Core

Configuring cache options correctly is the difference between caching that helps and caching that causes production incidents. Here is the full breakdown of [`MemoryCacheEntryOptions`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.caching.memory.memorycacheentryoptions):

```csharp
var cacheOptions = new MemoryCacheEntryOptions()
    .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
    .SetSlidingExpiration(TimeSpan.FromSeconds(30))
    .SetPriority(CacheItemPriority.Normal)
    .SetSize(1);
```

### Absolute Expiration

Defines a fixed time after which the cache entry expires, regardless of how often it is accessed. This prevents stale data from living in the cache indefinitely.

### Sliding Expiration

Sets a time window - if the entry is not accessed within this window, it expires. Each access resets the timer. Useful for "active session" type data.

> **Best practice:** I always set both sliding and absolute expiration together. Sliding alone is dangerous because a frequently accessed entry could live in the cache forever, serving increasingly stale data. My default: 30 seconds sliding, 5 minutes absolute.

### Priority

Determines which entries get evicted first when the cache hits its size limit. Options: `Low`, `Normal` (default), `High`, `NeverRemove`. Use `NeverRemove` sparingly - it means the entry will only be removed by expiration or manual `cache.Remove()`.

### Size

Assigns a "cost" to the entry. This works with `MemoryCacheOptions.SizeLimit` set during `AddMemoryCache()`. The cache tracks the total size of all entries and evicts lower-priority entries when the limit is reached.

### PostEvictionCallbacks

Registers a callback that fires when the entry is evicted for any reason - expiration, manual removal, or memory pressure:

```csharp
var options = new MemoryCacheEntryOptions()
    .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
    .SetSize(1)
    .RegisterPostEvictionCallback((key, value, reason, state) =>
    {
        // Log the eviction for monitoring - inject ILogger via closure or static reference
        logger.LogInformation("Cache entry '{CacheKey}' was evicted. Reason: {Reason}.", key, reason);
    });
```

This is invaluable for debugging cache behavior in production. I use it to track eviction rates and identify entries that expire too quickly. You can read more about how to set up proper logging in my [Serilog structured logging guide](/blog/structured-logging-with-serilog-in-aspnet-core/). Also, if you are using the [Options Pattern](/blog/options-pattern-in-aspnet-core/), you can bind cache configuration values from `appsettings.json` instead of hardcoding them.

## Cache Invalidation Strategies

Cache invalidation is where caching gets tricky. Serving stale data is worse than serving slow data. Here are the three main approaches:

### Time-Based Invalidation

The simplest strategy. Set expiration times and let the cache handle it:

- **Absolute Expiration** - entry expires after a fixed duration, no matter what
- **Sliding Expiration** - entry expires if not accessed within a window

I use time-based invalidation for data that changes infrequently and where brief staleness is acceptable - configuration values, category lists, permission sets.

### Manual Invalidation

Explicitly remove cache entries when the underlying data changes. This is what I use for write-through scenarios:

```csharp
public async Task<Product> CreateAsync(ProductCreationDto request, CancellationToken cancellationToken = default)
{
    var product = new Product(request.Name, request.Description, request.Price);
    await context.Products.AddAsync(product, cancellationToken);
    await context.SaveChangesAsync(cancellationToken);

    // Invalidate the list cache since a new product was added
    cache.Remove(AllProductsCacheKey);

    return product;
}
```

You can also update the cache instead of wiping it. This avoids the next request hitting a cold cache:

```csharp
// Instead of cache.Remove("products"), update the cached list
if (cache.TryGetValue("products", out List<Product>? cachedProducts) && cachedProducts is not null)
{
    cachedProducts.Add(product);
    // The list is a reference type - the cache already has the updated list.
    // No need to call cache.Set() again since IMemoryCache stores references, not copies.
    // Note: this pattern ONLY works with IMemoryCache. Distributed caches serialize data,
    // so mutating a local reference would not update the remote cache entry.
}
```

### Tag-Based Invalidation

This is where HybridCache shines. With `IMemoryCache`, there is no built-in way to invalidate all entries matching a tag. You would need to track keys manually. [HybridCache](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid) offers `RemoveByTagAsync("products")` out of the box - one of the reasons I recommend it for complex invalidation scenarios.

**My take on invalidation:** For most APIs, I use a simple pattern: absolute expiration for read-heavy data, manual removal on writes. I only build event-driven invalidation when I have multiple service instances sharing state - and at that point, I usually move to Redis or HybridCache.

## BenchmarkDotNet Performance Comparison

The old version of this article showed Postman response times (800ms to 40ms). That is useful for a rough demo, but it mixes network latency, JSON serialization, HTTP overhead, and actual cache performance. Let me show you proper benchmarks.

I set up a [BenchmarkDotNet](https://benchmarkdotnet.org/) project (included in the [companion repo](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/04-performance-and-caching/in-memory-caching-in-aspnet-core)) to measure the raw performance difference between a database fetch and a cache hit:

```csharp
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CacheBenchmarks
{
    private AppDbContext _dbContext = null!;
    private IMemoryCache _cache = null!;
    private Guid _productId;

    [GlobalSetup]
    public void Setup()
    {
        // Configure real PostgreSQL connection and pre-warm cache
        // Full setup in the repo's CacheBenchmarks.cs
    }

    [Benchmark(Baseline = true)]
    public async Task<Product?> SingleProduct_DatabaseFetch()
    {
        return await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == _productId);
    }

    [Benchmark]
    public Product? SingleProduct_CacheHit()
    {
        _cache.TryGetValue($"product:{_productId}", out Product? product);
        return product;
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
    public List<Product>? AllProducts_CacheHit()
    {
        _cache.TryGetValue("products", out List<Product>? products);
        return products;
    }
}
```

### Benchmark Results

| Method | Mean | Allocated |
|---|---|---|
| SingleProduct_DatabaseFetch | ~500 us | ~8 KB |
| SingleProduct_CacheHit | ~0.02 us | 0 B |
| AllProducts_DatabaseFetch (1000 items) | ~12,000 us | ~650 KB |
| AllProducts_CacheHit (1000 items) | ~0.02 us | 0 B |

> These are representative numbers from my local PostgreSQL setup. Your results will vary based on database location, query complexity, and hardware. Run the benchmarks yourself using the companion repo.

The numbers speak for themselves. A cache hit is roughly **25,000x faster** than a single-item database fetch, and **600,000x faster** for a list of 1,000 products. But the real win is not just speed - it is the **zero memory allocation**. Cache hits allocate 0 bytes because `IMemoryCache` returns the same object reference. No deserialization, no new objects, no GC pressure.

For comparison, I also wrote about [compiled queries in EF Core](/blog/ef-core-performance-mistakes/) which optimize the query translation pipeline. Compiled queries and caching solve different problems: compiled queries speed up query compilation, caching eliminates the database round-trip entirely. Use both on your hot paths for maximum throughput. You might also want to check out [tracking vs no-tracking queries](/blog/tracking-vs-no-tracking-queries-efcore/) to optimize the queries that do hit the database.

## Common Pitfalls and Troubleshooting

After using in-memory caching across dozens of projects, here are the pitfalls I see most often:

### Cache Stampede (Thundering Herd)

**Problem:** 100 concurrent requests hit a cache miss simultaneously. All 100 query the database.

**Cause:** `IMemoryCache` has no built-in stampede protection. `GetOrCreateAsync` can execute its factory delegate concurrently for the same key.

**Solution:** Use a `SemaphoreSlim` to ensure only one request populates the cache, or switch to HybridCache which has stampede protection built-in.

### Unbounded Cache Growth

**Problem:** Your API starts consuming excessive memory and eventually crashes with `OutOfMemoryException`.

**Cause:** `AddMemoryCache()` called without `SizeLimit`, and individual entries created without `SetSize()`.

**Solution:** Always configure `SizeLimit` on `MemoryCacheOptions` and assign a `Size` to every `MemoryCacheEntryOptions`. The cache will evict lower-priority entries when the limit is reached.

### Stale Data After Writes

**Problem:** Users create a new product, then immediately see the old product list.

**Cause:** The write operation did not invalidate the cached product list.

**Solution:** Call `cache.Remove(cacheKey)` in every write method that affects cached data. Make it part of your service pattern - never save to the database without considering which cache entries to invalidate.

### Cache Not Shared Across Instances

**Problem:** Users behind a load balancer get inconsistent data. Some see the latest data, others see stale cached data.

**Cause:** `IMemoryCache` is process-local. Each application instance maintains its own independent cache.

**Solution:** Move to distributed caching with [Redis](/blog/distributed-caching-in-aspnet-core-with-redis/) or use HybridCache with a Redis L2 backend. If brief inconsistency is acceptable, keep `IMemoryCache` with short absolute expiration. For a deeper look at handling errors when the cache or database layer fails, see my [global exception handling guide](/blog/global-exception-handling-in-aspnet-core/).

### Sliding Expiration Keeping Stale Data Alive

**Problem:** A heavily accessed cache entry never expires because the sliding window keeps resetting.

**Cause:** Only `SlidingExpiration` was set, without `AbsoluteExpiration`.

**Solution:** Always pair sliding with absolute expiration. The absolute time acts as a hard ceiling regardless of access frequency.

### Caching Null Values

**Problem:** `TryGetValue` returns `true` even though the value is `null`. The application treats this as a cache miss and queries the database on every request.

**Cause:** `IMemoryCache` stores the `null` reference as a valid cache entry.

**Solution:** Use `GetOrCreateAsync` (which handles this correctly) or check for null explicitly after `TryGetValue`.

## Key Takeaways

1. **In-memory caching with `IMemoryCache` is the fastest caching option in ASP.NET Core** because it stores data in process memory with zero network overhead and zero serialization.
2. **Always set both `SlidingExpiration` and `AbsoluteExpiration` together.** Sliding alone risks entries living forever in frequently accessed paths.
3. **Use `GetOrCreateAsync` for most caching scenarios.** It is cleaner than manual `TryGetValue` + `Set` and handles the cache-miss-then-populate pattern in one call.
4. **Set `SizeLimit` on `MemoryCacheOptions` to prevent unbounded memory growth.** Every cache entry needs a `Size` assigned or the limit is ignored.
5. **For multi-instance deployments, use distributed caching (Redis) or HybridCache.** `IMemoryCache` is process-local and cannot share state across pods.
6. **HybridCache (GA in .NET 9+) is the modern replacement** that adds stampede protection, tag-based invalidation, and optional L2 distributed caching with a simpler API.
7. **Invalidate cache entries on every write operation.** Stale data from missed invalidation is the #1 caching bug I see in production.

You can also use `BackgroundService` to refresh cache entries on a schedule. If your absolute expiration is 5 minutes, run a background task every 4 minutes to pre-warm the cache so users never hit a cold miss.

## Summary

In-memory caching is one of the highest-impact, lowest-effort performance optimizations you can add to an ASP.NET Core API. With BenchmarkDotNet confirming 25,000x+ speedups on cache hits and zero memory allocations, there is no reason to skip it on read-heavy endpoints.

The complete source code - including the BenchmarkDotNet project - is available in the [companion repository](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/04-performance-and-caching/in-memory-caching-in-aspnet-core).

If you found this helpful, share it with your colleagues. Happy Coding :)
