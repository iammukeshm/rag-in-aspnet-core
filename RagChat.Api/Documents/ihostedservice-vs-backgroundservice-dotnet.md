# IHostedService vs BackgroundService in .NET 10: Which to Use

**For 99% of long-running background work in .NET 10, use `BackgroundService`. For one-shot startup or shutdown tasks that do not need a loop, use `IHostedService` directly. For jobs that need persistence, retries, scheduling, or a dashboard, skip both and reach for Hangfire or Quartz.NET.**

Here is the short version. `BackgroundService` is an abstract base class that implements `IHostedService` for you. You override one method, `ExecuteAsync(CancellationToken stoppingToken)`, and the framework handles the lifecycle: starting your work when the host starts, signaling cancellation when the host stops, and waiting for your task to finish gracefully. `IHostedService` is the raw interface with `StartAsync` and `StopAsync` you implement yourself when you need a different shape, like a one-time database migration that runs at startup and does nothing while the app is running.

Across the 50+ .NET APIs I have shipped, almost every background task I have written has been a `BackgroundService`. Polling queues, sending scheduled emails, processing outbox events, refreshing in-memory caches: all `BackgroundService`. The handful of times I reached for raw `IHostedService` were for startup tasks: seed the database, register the app with a service discovery system, warm up an HTTP client pool. The handful of times I reached past both for Hangfire were when the team needed a UI dashboard, durable retries across restarts, or cron scheduling that `BackgroundService` would have meant hand-rolling badly.

In this article I will walk you through both abstractions, show the same task written both ways, walk through the 5 production gotchas I have personally hit with `BackgroundService` in .NET 6 through .NET 10 (including the one .NET 6+ default that quietly crashes your entire host process), and finish with a decision matrix you can apply on your next project. Let's get into it.

## IHostedService vs BackgroundService: Which Should You Use?

**Use `BackgroundService` for almost everything.** It is a base class that already implements `IHostedService` for the common case of a long-running loop, so you write one method instead of hand-rolling the lifecycle. Drop to raw `IHostedService` only when your work is not a loop - a one-shot task at startup or shutdown. When you need persistence, retries, cron scheduling, or a dashboard, neither one fits: reach for Hangfire or Quartz.NET instead.

| What you need | Reach for |
|---|---|
| A continuous loop (poll, heartbeat, outbox, cache refresh) | **BackgroundService** |
| A one-shot task at startup or shutdown (seed, migrate, register, drain) | **IHostedService** |
| Cron scheduling, retries, persistence across restarts, or a dashboard | **Hangfire / Quartz.NET** |
| Work triggered by an event rather than a timer | **Wolverine / MassTransit** |

The rest of this guide shows both abstractions side by side, walks through the 5 production gotchas that bite teams in real deployments, and ends with a full decision matrix.

## What is IHostedService in .NET 10?

**`IHostedService` is the low-level .NET interface for components that need to run alongside your application within the Generic Host lifecycle.** It defines two methods: `StartAsync(CancellationToken)` called when the host starts up, and `StopAsync(CancellationToken)` called when the host begins shutdown. Anything you register with `services.AddHostedService<T>()` is treated as part of the host and gets started before the app serves requests, and stopped before the host exits.

The interface is intentionally minimal:

```csharp
namespace Microsoft.Extensions.Hosting;

public interface IHostedService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

That is it. Two methods, both async, both passed a `CancellationToken`. .NET 10 has not changed the contract since `IHostedService` first shipped in .NET Core 2.0, with `BackgroundService` following in 2.1, which is part of why this abstraction has aged so well.

Microsoft's [official guidance for background tasks in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services) covers registration, lifetime, and shutdown behavior in detail. The key thing to internalize is that the host waits for `StartAsync` to complete before it serves the first request, and it waits up to a configurable timeout (`HostOptions.ShutdownTimeout`, 5 seconds by default in .NET 10) for `StopAsync` to complete before forcibly terminating. That makes hosted services a great fit for genuine startup work: ensure the database schema is migrated, register with a service registry, pre-warm a memory cache. It makes them a bad fit for fire-and-forget background work, because the host is genuinely waiting on you.

## What is BackgroundService in .NET 10?

**`BackgroundService` is an abstract base class that implements `IHostedService` for the common case of a long-running, continuously-running background task.** You inherit from it, override a single method (`ExecuteAsync`), and the framework handles everything else: starting the task in the background when the host starts, surfacing a `CancellationToken` (the `stoppingToken`) when the host shuts down, and waiting for your task to finish gracefully.

The signature you implement looks like this:

```csharp
public sealed class HeartbeatService(ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Heartbeat at {Time}", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
```

Notice what is happening. `BackgroundService` already implements `StartAsync` to kick off `ExecuteAsync` as a `Task` and return immediately, so the host can move on and serve requests. It already implements `StopAsync` to cancel the `stoppingToken` and wait for your task to complete. You did not write any of that lifecycle code. You wrote 6 lines of business logic.

**This is why `BackgroundService` exists.** 95% of background work in a typical .NET API is a loop that does something every N seconds or in response to a queue message. `BackgroundService` collapses that pattern into one method.

If you want to see exactly what `BackgroundService` is doing for you, [the entire implementation is about 80 lines on GitHub](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/BackgroundService.cs). It is worth reading once just so you know there is no magic.

## Same Task, Both Ways: A Side-by-Side Comparison

To make the difference concrete, here is the same trivial task (log a heartbeat every 30 seconds) written first with raw `IHostedService`, then with `BackgroundService`. The differences are everything you need to know about why I almost always reach for `BackgroundService`.

**With raw IHostedService:**

```csharp
public sealed class HeartbeatHostedService(ILogger<HeartbeatHostedService> logger) : IHostedService
{
    private Task? _backgroundTask;
    private CancellationTokenSource? _stoppingCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = RunAsync(_stoppingCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_backgroundTask is null) return;

        try
        {
            _stoppingCts!.Cancel();
        }
        finally
        {
            await Task.WhenAny(_backgroundTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Heartbeat at {Time}", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
```

That is roughly 30 lines for "log a heartbeat every 30 seconds." Most of it is lifecycle plumbing: linking the cancellation tokens, holding a reference to the task, racing the task against the `StopAsync` token so a slow shutdown does not hang the host indefinitely. Every one of those lines is a place where I have introduced a bug at some point.

**With BackgroundService:**

```csharp
public sealed class HeartbeatBackgroundService(ILogger<HeartbeatBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Heartbeat at {Time}", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
```

6 lines. Same behavior. `BackgroundService` is doing the exact lifecycle plumbing the first example did, but in the framework instead of in your codebase.

**My take:** If your background task is a loop, write a `BackgroundService`. The only honest reason to implement `IHostedService` directly is when your work does not fit the loop shape, which I will cover in the gotcha section. Otherwise you are writing lifecycle plumbing that Microsoft has already shipped and battle-tested for 7+ years.

## Registering and the Lifecycle You Actually Care About

Registration is one line either way:

```csharp
builder.Services.AddHostedService<HeartbeatBackgroundService>();
```

`AddHostedService<T>()` registers your service as a singleton implementation of `IHostedService`. The framework resolves it once at host startup and calls `StartAsync` on it.

The full lifecycle in .NET 10 looks like this:

1. **Host starts** (`app.Run()` or `host.RunAsync()` is called).
2. Framework resolves every registered `IHostedService` and calls `StartAsync` on each, **in registration order**.
3. For a `BackgroundService`, `StartAsync` kicks off `ExecuteAsync` as a fire-and-forget `Task` and returns immediately, so the host is not blocked waiting for your loop.
4. **Host runs** and serves requests. Your background task runs in parallel.
5. **Host receives shutdown signal** (Ctrl+C, SIGTERM, `IHostApplicationLifetime.StopApplication()`).
6. Framework calls `StopAsync` on each `IHostedService`, **in reverse registration order**.
7. For a `BackgroundService`, `StopAsync` signals the `stoppingToken` and waits for `ExecuteAsync` to return (up to `HostOptions.ShutdownTimeout`, default 5 seconds).
8. After all hosted services have stopped, the host exits.

The reverse-order shutdown matters. If `ServiceA` depends on `ServiceB`, register `B` first then `A`. On shutdown, `A` will stop first (when `B` is still running and can be called from `A`'s cleanup), then `B` will stop. This is a quiet but important guarantee, and I have seen teams reorder registrations not knowing they were breaking it.

## The 5 Production Gotchas I Have Hit With BackgroundService

This is the part nobody talks about until you have shipped enough background services in production. Every one of these has cost me a real incident.

### Gotcha 1: An Unhandled Exception in ExecuteAsync Can Crash the Entire Host

**This is the most expensive gotcha on this list, and I have caused exactly one production outage with it.**

Before .NET 6, an unhandled exception in `ExecuteAsync` was logged and your `BackgroundService` would silently stop running. Bad, but at least your API stayed up.

In .NET 6 and later, the default behavior changed. If `ExecuteAsync` throws, the entire host process terminates. That means your whole API goes down because one background task threw an `HttpRequestException` or a transient `SqlException`.

The default is controlled by [`BackgroundServiceExceptionBehavior`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.backgroundserviceexceptionbehavior), and in .NET 6+ the default is `StopHost`. The other option is `Ignore`, which restores the pre-.NET 6 behavior of logging and continuing.

I learned this at 3 AM when an email-sending `BackgroundService` threw a transient SMTP error and took down the entire orders API I was on-call for. Here is the config flag that prevents this:

```csharp
builder.Services.Configure<HostOptions>(opts =>
{
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});
```

But changing the global flag is not enough. The right pattern is to wrap your `ExecuteAsync` body in a try/catch so transient failures do not stop the loop:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await SendQueuedEmailsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Email loop iteration failed. Continuing.");
        }

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
    }
}
```

**My take:** For most production APIs, set `BackgroundServiceExceptionBehavior` to `Ignore` AND wrap the body in try/catch. You almost never want a transient background error to crash the entire host.

### Gotcha 2: Injecting Scoped Services Into a BackgroundService (Captive Dependency)

A `BackgroundService` is registered as a singleton. If you inject a scoped service (like `DbContext`) directly into the constructor, you have created a captive dependency: the scoped service lives for the entire lifetime of the host, not per-iteration.

**Bad:**

```csharp
public sealed class OrderProcessor(MyDbContext db, ILogger<OrderProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var orders = await db.Orders.Where(o => !o.Processed).ToListAsync(stoppingToken);
            // db is the SAME instance for every iteration of this loop, forever.
            // Change tracking grows unbounded. First SQL exception poisons it for life.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
```

This will work in development and break in production. The `DbContext` accumulates tracked entities across iterations, memory grows, and the first transient SQL exception leaves the context in a broken state that no `Where(...).ToListAsync()` call can recover from.

**Better:** Inject `IServiceScopeFactory` and create a fresh scope per iteration.

```csharp
public sealed class OrderProcessor(IServiceScopeFactory scopeFactory, ILogger<OrderProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MyDbContext>();

            var orders = await db.Orders
                .Where(o => !o.Processed)
                .ToListAsync(stoppingToken);
            // db is disposed at end of iteration. Next loop gets a fresh one.

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
```

This is the same pattern Microsoft's docs recommend, and it is the single most common bug I see in .NET background services. The compiler does not stop you from injecting `DbContext` directly into a singleton, so the bug compiles cleanly and only surfaces under sustained load.

### Gotcha 3: ExecuteAsync Returning Early Silently Kills the Service Forever

A `BackgroundService` is only "running" while its `ExecuteAsync` task is alive. The moment `ExecuteAsync` returns, the framework marks the service as completed. It is not called again. It will not retry. Your service is dead until the host restarts, and unless you logged something, you will have no idea.

There are three common ways this happens in real codebases:

1. An exception escapes whatever catch you have and propagates out of the loop.
2. The loop exits because of a boolean flag or counter you forgot was there ("just running this 10 times for a test").
3. An awaited `Task` returns a faulted result that gets re-thrown out of the outer `try`.

The fix is defensive: log when `ExecuteAsync` enters AND exits, and wrap the body to distinguish expected cancellation from unexpected exits.

**Better:**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    logger.LogInformation("{Service} ExecuteAsync starting", nameof(OrderProcessor));
    try
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOneBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Iteration failed. Continuing the loop.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        // expected during graceful shutdown - do not log as error
    }
    finally
    {
        logger.LogInformation("{Service} ExecuteAsync exiting", nameof(OrderProcessor));
    }
}
```

The `finally` block is the alarm. If you see `ExecuteAsync exiting` in the logs at 2 PM (not during a deploy), you know the service has gone silent and you have minutes, not hours, to react. I have seen `BackgroundService` instances die silently for days because nobody logged the exit, and the only symptom was "the outbox is not draining."

A historical note for anyone who has seen older guidance: pre-.NET 6, there was a subtle pitfall where heavy synchronous work in `ExecuteAsync` before the first `await` could block `StartAsync` from returning. The modern `BackgroundService` implementation wraps `ExecuteAsync` in `Task.Run(...)` and unconditionally returns `Task.CompletedTask` from `StartAsync`, so this is no longer a concern. The `await Task.Yield()` pattern you may see in older articles is now defensive ceremony, not a fix for a real bug.

### Gotcha 4: Ignoring stoppingToken Slows Shutdown and Abandons In-Flight Work

`HostOptions.ShutdownTimeout` defaults to 5 seconds in .NET 10. If your `ExecuteAsync` ignores the `stoppingToken` and just keeps working, `StopAsync` waits the full 5 seconds, then returns and the host continues shutting down. Your background task is abandoned mid-iteration: any in-flight DB write that has not yet committed is lost, any unflushed log line never appears, any half-sent HTTP request stays half-sent.

Every `await` and every loop condition should honor `stoppingToken`:

**Good:**

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    await DoWorkAsync(stoppingToken);
    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
}
```

**Bad:**

```csharp
while (true)
{
    DoWork();                    // no token, no async, no cancellation
    Thread.Sleep(10_000);        // blocks the thread, ignores stoppingToken entirely
}
```

`Thread.Sleep(10_000)` will not unblock when shutdown is signaled. The host's shutdown sequence pauses for up to 5 seconds waiting for `ExecuteAsync` to honor cancellation, then gives up. If your "iteration" was mid-way through writing to a database when shutdown fired, that write is now lost.

If your iterations genuinely need longer than 5 seconds to finish cleanly (say, draining a queue batch), extend the timeout explicitly so `StopAsync` waits for the work:

```csharp
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(30);
});
```

But extending the timeout is almost always the wrong fix. Honoring `stoppingToken` and keeping each iteration short is the right one.

### Gotcha 5: Heavy CPU Work in ExecuteAsync Starves the Thread Pool

`ExecuteAsync` runs on a `ThreadPool` thread. If you do CPU-bound work that does not yield (no `await`, no `Task.Delay`), you hold that thread until you finish. With enough background services doing this, you starve the entire thread pool, and requests to your API start timing out for no obvious reason.

The fix has two flavors:

1. **For genuinely heavy compute**, move the CPU-bound work off to a long-running task: `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`. This gets you a dedicated thread instead of a thread-pool thread.
2. **For chunkable work**, insert `await Task.Yield()` or `await Task.Delay(1, stoppingToken)` between batches so the thread pool can serve other requests.

Honestly though, if the work is CPU-bound for minutes at a time, a `BackgroundService` inside your API process is the wrong shape. Push the work to a queue (SQS, Service Bus, Kafka) and let a separate worker process handle it. Hosted services inside an API process should be light: poll, dispatch, dequeue. The actual heavy work belongs somewhere else.

## When NOT to Use Hosted Services (Reach for Hangfire/Quartz/Wolverine)

Hosted services are great for in-process, app-lifetime background work. They are a bad fit for jobs that need any of these:

- **Persistence across restarts** - the job survives a deploy or a pod restart and resumes
- **Cron scheduling** - "every Monday at 9 AM IST," not "every N seconds"
- **A dashboard** - visibility into what is queued, running, failed
- **Distributed coordination** - multiple API instances behind a load balancer should not run the same scheduled job 3 times
- **Long-running jobs** - anything that takes longer than the shutdown grace period

For any of those, reach for the appropriate dedicated tool:

- **[Hangfire](https://www.hangfire.io/)** - Job queue with persistence (SQL or Redis), retries, dashboard, recurring jobs. Production-grade for most APIs that need durable background work. This is what I reach for 80% of the time when `BackgroundService` is not enough.
- **[Quartz.NET](https://www.quartz-scheduler.net/)** - Heavy-duty scheduling with cron expressions, calendars, time-zone support. Overkill for simple polling, ideal for complex schedules.
- **[Wolverine](https://wolverinefx.io/)** or **MassTransit** - Message-bus-driven background work. If the trigger is "an event happened" not "every N seconds," this is the right shape.

**My take:** If you find yourself building durability, retry logic, persistence, or a dashboard on top of `BackgroundService`, you have reinvented Hangfire badly. Use Hangfire.

## My Take

Default to `BackgroundService` for any continuous in-process loop. The lifecycle plumbing is solved, the cancellation semantics are correct, and the framework will outlive any custom wrapper I might write. I do not implement `IHostedService` directly unless I have a specific reason (one-shot startup task, non-loop shape).

For the 5 gotchas above, I treat the following as muscle memory on every new `BackgroundService`:

1. Set `BackgroundServiceExceptionBehavior = Ignore` globally AND wrap the `ExecuteAsync` body in try/catch.
2. Never inject `DbContext` or any scoped service into the constructor. Always inject `IServiceScopeFactory` and create a scope per iteration.
3. Log on entry AND exit of `ExecuteAsync`. A `finally` block with an exit log catches every silent death.
4. Pass `stoppingToken` to every `await` and every loop condition. Never `Thread.Sleep`.
5. If the work is CPU-bound or might run longer than a few seconds per iteration, push it to a queue and have a separate worker handle it.

The moment any of these patterns starts feeling cramped, I know I have outgrown hosted services. That is the signal to reach for Hangfire (durable jobs), Quartz (complex scheduling), or Wolverine (message-driven workflows). The `BackgroundService` abstraction is for light in-process work. The moment you need durability or distribution, the right tool changes.

## Decision Matrix: Pick the Right Background Abstraction

Use this matrix on your next .NET 10 project when something has to run in the background.

| Use case | IHostedService | BackgroundService | Hangfire | Quartz.NET | Wolverine |
|---|---|---|---|---|---|
| Continuous loop (poll / heartbeat / outbox processor) | ❌ | ✅ | overkill | ❌ | only if event-driven |
| One-shot startup task (seed, migrate, register) | ✅ | ❌ | ❌ | ❌ | ❌ |
| One-shot shutdown task (drain, deregister) | ✅ | ❌ | ❌ | ❌ | ❌ |
| Scheduled job with cron expression | ❌ | hand-roll badly | basic cron | ✅ | basic cron |
| Job queue with persistence + retries | ❌ | ❌ | ✅ | ❌ | ✅ |
| Need a UI dashboard for ops | ❌ | ❌ | ✅ | ❌ | ✅ |
| Multi-instance coordination (no duplicate runs) | ❌ | ❌ | ✅ | ✅ | ✅ |
| Event-driven background work | ❌ | only with manual wiring | ❌ | ❌ | ✅ |
| Lightweight in-process work | ✅ | ✅ | overkill | overkill | overkill |
| Survives an app restart | ❌ | ❌ | ✅ | ✅ | ✅ |
| Heavy CPU work for minutes | ❌ | ❌ (move to worker) | ✅ via Server queues | ✅ | ✅ |

If your row points to ❌ on both `IHostedService` and `BackgroundService`, that is the signal to escalate to Hangfire, Quartz, or a message bus. Do not invent durability on top of `BackgroundService`.

## Key Takeaways

- `IHostedService` is the raw 2-method interface. `BackgroundService` is the abstract base class that implements `IHostedService` for continuous-loop work.
- Use `BackgroundService` for 99% of background work in .NET 10. Use raw `IHostedService` for one-shot startup or shutdown tasks. Use Hangfire / Quartz / Wolverine when you need durability, scheduling, or distribution.
- In .NET 6 and later, an unhandled exception in `ExecuteAsync` crashes the entire host process by default. Set `BackgroundServiceExceptionBehavior` to `Ignore` and wrap the loop body in try/catch.
- Never inject `DbContext` or other scoped services directly into a `BackgroundService` constructor. Inject `IServiceScopeFactory` and create a fresh scope per iteration.
- Log on entry AND exit of every `ExecuteAsync`. A `finally` block with an exit log catches silent service deaths that would otherwise stay invisible for hours or days.
- Always honor `stoppingToken` on every `await` and every loop condition. Ignoring it abandons in-flight work and stretches shutdown by up to 5 seconds.

## FAQ

## Troubleshooting

Common issues that trip up teams new to hosted services. Quick fixes here, with deeper context above.

**"My BackgroundService logs nothing and the API process exits."** You have hit Gotcha 1. `ExecuteAsync` threw an unhandled exception and the default `BackgroundServiceExceptionBehavior` in .NET 6+ killed the host. Wrap the loop body in try/catch, log the exception, and set the global behavior to `Ignore`.

**"My BackgroundService stopped working at some point and I cannot tell when."** You have hit Gotcha 3. `ExecuteAsync` returned, the framework marked the service as completed, and no further iterations ran. Add an entry log AND a `finally`-block exit log. The exit log appearing outside a deploy window is the signal that the service died.

**"My DbContext throws ObjectDisposedException after a few iterations."** You have hit Gotcha 2. You injected `DbContext` directly into a singleton `BackgroundService`. Replace with `IServiceScopeFactory` and create a scope per iteration.

**"My pod takes a few extra seconds to terminate on deploy and in-flight work is lost."** You have hit Gotcha 4. Your loop ignores `stoppingToken`. Pass it to every `await` and check it in the loop condition. Never `Thread.Sleep`. The host gives you up to `HostOptions.ShutdownTimeout` (5 seconds by default) to honor cancellation; after that, the iteration is abandoned.

**"API requests start timing out under load even though CPU looks normal."** You have hit Gotcha 5. CPU-bound work in `ExecuteAsync` is holding ThreadPool threads. Either move the work off-pool with `TaskCreationOptions.LongRunning` or push it out of the API process entirely.

**"My BackgroundService runs in development but not production."** Three usual suspects. Check that the host is using `host.RunAsync()` and not `host.Start()` followed by an early exit. Check that the deploy environment is actually running the same compose / Helm chart that registers the service. And check the logs for an exception inside `ExecuteAsync` that the default `StopHost` behavior swallowed by killing the process.

## Summary

`IHostedService` and `BackgroundService` are the .NET 10 abstractions for any work that needs to run alongside your application within the Generic Host lifecycle. `BackgroundService` is the right default for any continuous loop. `IHostedService` is the right tool for one-shot startup or shutdown work that does not fit the loop shape. Both are battle-tested, both have been stable since .NET Core 2.1, and both have idiomatic fixes for the 5 production gotchas I have walked through above.

For 99% of in-process background work, write a `BackgroundService`. Wrap the body in try/catch. Inject `IServiceScopeFactory` instead of scoped services directly. Add `await Task.Yield()` at the top of `ExecuteAsync`. Honor `stoppingToken` on every await. Keep the work light. The moment you need durability across restarts, cron scheduling, a dashboard, or multi-instance coordination, escalate to Hangfire, Quartz, or a message bus. The hosted-service abstraction was never designed to give you any of those.

If this kind of "here is the abstraction, here are the 5 gotchas I have actually hit" is useful, that is the format I write every Tuesday in the newsletter. Subscribe below.

Happy Coding :)
