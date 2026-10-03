# Options Pattern in ASP.NET Core - IOptions vs Snapshot vs Monitor

I once shipped a feature flag system that read from `appsettings.json` using `IOptions<FeatureFlagOptions>`. Everything worked perfectly in development. But in production, when the DevOps team toggled a feature flag by updating `appsettings.json`, nothing happened. The API kept returning the old values. Users were stuck on the old behavior, and I had to restart the application to pick up the change. The fix? Replacing `IOptions<T>` with `IOptionsMonitor<T>` - a one-line change that cost me 3 hours of debugging and an incident report. That experience taught me that understanding the differences between `IOptions`, `IOptionsSnapshot`, and `IOptionsMonitor` is not optional - it is essential for any production ASP.NET Core application.

In this guide, I will walk you through the Options Pattern in ASP.NET Core .NET 10 from the ground up - binding configuration to strongly-typed classes, validating with data annotations and custom validators, reloading configuration at runtime, named options, PostConfigure, and a decision matrix for choosing the right options interface. Let's get into it.

## What Is the Options Pattern in ASP.NET Core?

**The Options Pattern is a design pattern in ASP.NET Core that binds configuration sections from `appsettings.json` (or any configuration source) to strongly-typed C# classes, providing type safety, validation, and dependency injection support.** Instead of reading raw key-value strings from `IConfiguration`, you define a class that mirrors your configuration structure and let the framework handle the binding.

According to [Microsoft's official documentation on the Options Pattern](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0), the pattern provides a mechanism to validate configuration data and uses dependency injection to make configuration available throughout your application.

The Options Pattern solves three problems that raw `IConfiguration` cannot:

1. **Type safety** - configuration values are bound to strongly-typed properties, so you get compile-time checks instead of runtime string parsing
2. **Validation** - data annotations, custom validators, and startup validation ensure bad configuration fails fast
3. **Reloading** - `IOptionsSnapshot<T>` and `IOptionsMonitor<T>` pick up configuration changes without restarting the application

## Why Not Just Use IConfiguration?

Before the Options Pattern, most developers injected `IConfiguration` directly and pulled values with string keys. Here is what that looks like:

```csharp
public class WeatherController(IConfiguration configuration) : ControllerBase
{
    [HttpGet("config")]
    public IActionResult Get()
    {
        var city = configuration.GetValue<string>("WeatherOptions:City");
        var state = configuration.GetValue<string>("WeatherOptions:State");
        var temperature = configuration.GetValue<int>("WeatherOptions:Temperature");
        var summary = configuration.GetValue<string>("WeatherOptions:Summary");
        return Ok(new { City = city, State = state, Temperature = temperature, Summary = summary });
    }
}
```

This works, but it has real problems:

- **No validation** - if `Temperature` is set to `"banana"`, you get a silent default of `0` instead of an error
- **No type safety** - misspelling `"WeatherOptions:Citty"` compiles just fine but returns `null` at runtime
- **Security risk** - `IConfiguration` has access to *every* configuration value, including connection strings and secrets that your weather endpoint should never see
- **No reloading** - you are reading raw strings, not reactive options
- **No default values** - if a key is missing, you get `null` or `default` with no way to set a fallback

My take: `IConfiguration` is fine for reading a single value in `Program.cs` during startup. For anything injected into services or controllers, use the Options Pattern. Trust me, the 2 minutes it takes to set up a strongly-typed options class will save you hours of debugging magic string issues in production.

## Getting Started with the Options Pattern

Setting up the Options Pattern takes three steps: define the options class, bind it to configuration, and inject it where you need it.

### Step 1: Define the Options Class

Create a class whose properties match the JSON structure in `appsettings.json`:

```json
"WeatherOptions": {
  "City": "Trivandrum",
  "State": "Kerala",
  "Temperature": 22,
  "Summary": "Warm"
}
```

```csharp
public class WeatherOptions
{
    public const string SectionName = "WeatherOptions";

    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int Temperature { get; set; }
    public string? Summary { get; set; }
}
```

I always add a `const string SectionName` to avoid magic strings during registration. It costs nothing and prevents typos.

### Step 2: Bind and Register in Program.cs

In your `Program.cs`, bind the options class to the configuration section:

```csharp
builder.Services.AddOptions<WeatherOptions>()
    .BindConfiguration(WeatherOptions.SectionName);
```

This single line registers `IOptions<WeatherOptions>` in the DI container and binds it to the `"WeatherOptions"` section of your configuration. The `.BindConfiguration()` method is the modern .NET 10 way to bind options - it is cleaner than the older `.Configure<T>(builder.Configuration.GetSection(...))` approach.

### Step 3: Inject and Use

Now you can inject `IOptions<WeatherOptions>` into any controller, service, or middleware:

```csharp
public class WeatherController(IOptions<WeatherOptions> options) : ControllerBase
{
    [HttpGet("options")]
    public IActionResult GetFromOptions()
    {
        return Ok(options.Value);
    }
}
```

Access the configuration values through `options.Value`. That is it - strongly-typed, dependency-injected, zero magic strings. Quite handy, yeah?

For minimal APIs, the same injection works directly in endpoint handlers:

```csharp
app.MapGet("/api/weather", (IOptions<WeatherOptions> options) =>
{
    return Results.Ok(options.Value);
});
```

## Validating Configuration with Data Annotations

Configuration errors are one of the most frustrating bugs to debug. The value is wrong in `appsettings.json`, but the error does not surface until some random user hits the right endpoint at the wrong time. Data annotations let you catch these problems immediately.

Add validation attributes to your options class:

```csharp
using System.ComponentModel.DataAnnotations;

public class WeatherOptions
{
    public const string SectionName = "WeatherOptions";

    [Required(AllowEmptyStrings = false)]
    public string City { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string State { get; set; } = string.Empty;

    [Range(0, 100)]
    public int Temperature { get; set; }

    public string? Summary { get; set; }
}
```

Then enable data annotation validation in your registration:

```csharp
builder.Services.AddOptions<WeatherOptions>()
    .BindConfiguration(WeatherOptions.SectionName)
    .ValidateDataAnnotations();
```

Now, if `City` is empty or `Temperature` is set to `-10`, ASP.NET Core throws an `OptionsValidationException` when the options are first resolved. The error message tells you exactly which property failed and why.

## Startup Validation with ValidateOnStart

Data annotation validation only fires when the options are first resolved - meaning when the first request that needs `IOptions<WeatherOptions>` comes in. That is too late. If your production app starts with invalid configuration, you want it to fail immediately, not after the first user hits a specific endpoint.

According to [Microsoft's configuration documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0), eager validation ensures configuration errors are caught before the application starts serving requests. `.ValidateOnStart()` solves this:

```csharp
builder.Services.AddOptions<WeatherOptions>()
    .BindConfiguration(WeatherOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

With this, the application will fail to start if configuration validation fails. You will see the validation errors in your terminal output before the application even begins accepting requests. This is exactly what you want in production - fail fast, fail loud.

I use `.ValidateOnStart()` on every options registration in every project. There is no reason not to. The cost is negligible, and catching a misconfigured `appsettings.json` at deployment time instead of at 3 AM is worth it.

## Custom Validation with IValidateOptions&lt;T&gt;

Data annotations handle simple rules like `[Required]` and `[Range]`. But what about business-specific validation? What if `City` must be from a known list, or `Temperature` and `Summary` must be consistent? That is where `IValidateOptions<T>` comes in - the modern, testable approach to custom options validation in .NET 10.

```csharp
using Microsoft.Extensions.Options;

public class WeatherOptionsValidator : IValidateOptions<WeatherOptions>
{
    public ValidateOptionsResult Validate(string? name, WeatherOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.City))
        {
            failures.Add("City is required and cannot be empty.");
        }

        if (options.Temperature is < 0 or > 100)
        {
            failures.Add($"Temperature must be between 0 and 100. Got: {options.Temperature}.");
        }

        if (!string.IsNullOrWhiteSpace(options.Summary) && options.Summary.Length > 200)
        {
            failures.Add("Summary must be 200 characters or fewer.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
```

Register the validator in your DI container:

```csharp
builder.Services.AddSingleton<IValidateOptions<WeatherOptions>, WeatherOptionsValidator>();
```

Why is `IValidateOptions<T>` better than inline `.Validate()` lambdas? Three reasons:

1. **Testable** - you can unit test the validator class independently without spinning up a host
2. **DI-aware** - the validator can inject other services (database lookups, HTTP clients) for complex validation
3. **Separation of concerns** - validation logic lives in its own file, not buried in `Program.cs`

The older approach using `.Validate(options => ...)` inline lambdas still works, but for anything beyond a one-line check, I recommend `IValidateOptions<T>`. It scales better and is easier to maintain.

## Reloading Configuration - IOptions vs IOptionsSnapshot vs IOptionsMonitor

This is the section that would have saved me 3 hours of debugging. ASP.NET Core provides three interfaces for reading options, and choosing the wrong one leads to subtle production bugs.

### IOptions&lt;T&gt; - Read Once, Never Reload

`IOptions<T>` is registered as a **singleton**. It builds the value the first time you read `.Value` and caches it for the entire application lifetime. If someone updates `appsettings.json` while the app is running, `IOptions<T>` will not see the change.

```csharp
public class WeatherController(IOptions<WeatherOptions> options) : ControllerBase
{
    [HttpGet("options")]
    public IActionResult GetFromOptions()
    {
        // Always returns the startup values, even if appsettings.json changes
        return Ok(options.Value);
    }
}
```

### IOptionsSnapshot&lt;T&gt; - Reload Per Request

`IOptionsSnapshot<T>` is registered as **scoped**. It creates a new snapshot of the options at the beginning of each HTTP request. If `appsettings.json` changes between requests, the next request will see the updated values. But within a single request, the values are consistent.

```csharp
public class WeatherController(IOptionsSnapshot<WeatherOptions> optionsSnapshot) : ControllerBase
{
    [HttpGet("snapshot")]
    public IActionResult GetFromSnapshot()
    {
        // Returns fresh values from appsettings.json on each request
        return Ok(optionsSnapshot.Value);
    }
}
```

Because `IOptionsSnapshot<T>` is scoped, you should not inject it into singleton services. What happens depends on the environment. In Development, scope validation stops the app at startup with `` Cannot consume scoped service 'Microsoft.Extensions.Options.IOptionsSnapshot`1[WeatherOptions]' from singleton 'WeatherCache'. `` In Production that validation is off, so the app starts and the singleton keeps the first snapshot forever. I tested this on .NET 10: after changing `Temperature` from 22 to 35, the singleton still returned 22 while a fresh `IOptionsSnapshot` returned 35. No error, just stale config.

### IOptionsMonitor&lt;T&gt; - Real-Time Reload

`IOptionsMonitor<T>` is registered as a **singleton** but provides the current value at the moment you access `.CurrentValue`. According to [Microsoft's IOptionsMonitor documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.options.ioptionsmonitor-1?view=net-10.0), it also supports change notifications through the `OnChange` callback.

```csharp
public class WeatherController(IOptionsMonitor<WeatherOptions> optionsMonitor) : ControllerBase
{
    [HttpGet("monitor")]
    public IActionResult GetFromMonitor()
    {
        // Returns the current value at the exact moment of access
        return Ok(optionsMonitor.CurrentValue);
    }
}
```

The key difference from `IOptionsSnapshot<T>`: if the configuration changes *during* a request, `IOptionsMonitor<T>` will reflect the change mid-request. `IOptionsSnapshot<T>` will not - it locks the values at request start.

### See the Difference in Action

Here is a controller that compares all three side by side:

```csharp
public class WeatherController(
    IOptions<WeatherOptions> options,
    IOptionsSnapshot<WeatherOptions> optionsSnapshot,
    IOptionsMonitor<WeatherOptions> optionsMonitor) : ControllerBase
{
    [HttpGet("compare")]
    public IActionResult CompareAll()
    {
        return Ok(new
        {
            IOptions = options.Value,
            IOptionsSnapshot = optionsSnapshot.Value,
            IOptionsMonitor = optionsMonitor.CurrentValue
        });
    }
}
```

Start the app, call `/api/weather/compare`. Then change the `Temperature` in `appsettings.json` from `22` to `35` *without restarting the app*. Call the endpoint again. You will see `IOptions` still shows `22`, while `IOptionsSnapshot` and `IOptionsMonitor` both show `35`.

## Which Options Interface Should You Use? Decision Matrix

This is the question I get asked most often, so here is my definitive recommendation:

| Criteria | IOptions&lt;T&gt; | IOptionsSnapshot&lt;T&gt; | IOptionsMonitor&lt;T&gt; |
|----------|-------------------|--------------------------|--------------------------|
| **Service Lifetime** | Singleton | Scoped | Singleton |
| **Reads Config** | Once at startup | Per HTTP request | On every `.CurrentValue` access |
| **Supports Reloading** | No | Yes | Yes |
| **Change Notifications** | No | No | Yes (`OnChange`) |
| **Can Inject Into Singletons** | Yes | No | Yes |
| **Named Options** | No | Yes (`.Get("name")`) | Yes (`.Get("name")`) |
| **Performance** | Best (cached) | Good (per-request) | Good (per-access) |
| **Best For** | Static config | Request-scoped config | Feature flags, dynamic config |

**My take: default to `IOptions<T>`.** Most configuration values - connection strings, API URLs, app settings - do not change at runtime. `IOptions<T>` is the simplest, fastest, and most common choice. Only reach for `IOptionsSnapshot<T>` or `IOptionsMonitor<T>` when you have a specific need for runtime reloading.

Use `IOptionsMonitor<T>` when:
- You have feature flags that change without redeployment
- You use dynamic configuration sources (Azure App Configuration, AWS Parameter Store)
- You need the `OnChange` callback to trigger side effects when config changes

Use `IOptionsSnapshot<T>` when:
- You need reloading but want request-level consistency (same values throughout a single request)
- You are already in a scoped context (controllers, scoped services)

## Named Options

Sometimes you need multiple configurations of the same type. For example, your application might connect to two different notification providers, each with their own settings. Named options let you register and retrieve multiple instances of the same options class by name.

Register named options in `Program.cs`:

```csharp
builder.Services.AddOptions<NotificationOptions>("Email")
    .BindConfiguration("NotificationOptions:Email")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<NotificationOptions>("Sms")
    .BindConfiguration("NotificationOptions:Sms")
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

Retrieve them using `IOptionsSnapshot<T>` or `IOptionsMonitor<T>`:

```csharp
public class NotificationService(IOptionsSnapshot<NotificationOptions> optionsSnapshot)
{
    public void SendEmail()
    {
        var emailOptions = optionsSnapshot.Get("Email");
        // Use emailOptions.SmtpHost, emailOptions.From, etc.
    }

    public void SendSms()
    {
        var smsOptions = optionsSnapshot.Get("Sms");
        // Use smsOptions.Provider, smsOptions.ApiKey, etc.
    }
}
```

Note that `IOptions<T>` does not support named options - it always returns the default (unnamed) instance. You need `IOptionsSnapshot<T>` or `IOptionsMonitor<T>` to use `.Get("name")`.

## PostConfigure - Overriding Options After Binding

`PostConfigure<T>` runs *after* all configuration binding and `Configure<T>` calls have completed. It is your last chance to modify options values before they are served to consumers. This is useful for computed defaults, environment-specific overrides, or filling in values that depend on other configuration.

```csharp
builder.Services.PostConfigure<WeatherOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.Summary))
    {
        options.Summary = options.Temperature switch
        {
            <= 10 => "Cold",
            <= 25 => "Warm",
            _ => "Hot"
        };
    }
});
```

In this example, if `Summary` is not provided in `appsettings.json`, it gets computed from the `Temperature` value. This keeps your configuration file lean while ensuring your application always has meaningful defaults.

`PostConfigure` also accepts named options:

```csharp
builder.Services.PostConfigure<NotificationOptions>("Email", options =>
{
    options.From ??= "default@example.com";
});
```

## How to Access Options in Program.cs

Sometimes you need configuration values during startup, before the DI container is built. The Options Pattern is not available yet at that point, so you read from `IConfiguration` directly:

```csharp
var weatherOptions = builder.Configuration
    .GetSection(WeatherOptions.SectionName)
    .Get<WeatherOptions>();

Console.WriteLine($"Starting with City: {weatherOptions?.City}");
```

This is the one place where using `IConfiguration` directly is the right call. Once `builder.Build()` has been called, switch to `IOptions<T>` and friends.

You can also access options from the built `app` using the service provider:

```csharp
var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<WeatherOptions>>();
Console.WriteLine($"City: {options.Value.City}");
```

## BindConfiguration vs Configure - What is the Difference?

You will see two patterns for binding options in ASP.NET Core code. Here is when to use each:

```csharp
// Modern approach (recommended in .NET 10)
builder.Services.AddOptions<WeatherOptions>()
    .BindConfiguration(WeatherOptions.SectionName);

// Older approach (still works, more verbose)
builder.Services.Configure<WeatherOptions>(
    builder.Configuration.GetSection(WeatherOptions.SectionName));
```

Both do the same thing - bind a configuration section to an options class. The difference:

- `.BindConfiguration()` is chainable with `.ValidateDataAnnotations()`, `.ValidateOnStart()`, and `.Validate()`
- `.Configure<T>()` returns `IServiceCollection`, so you cannot chain validation methods directly
- `.BindConfiguration()` automatically supports configuration reloading
- `.Configure<T>()` is from the older `Microsoft.Extensions.Options.ConfigurationExtensions` API

My take: use `.AddOptions<T>().BindConfiguration()` for all new .NET 10 code. It is cleaner, supports validation chaining, and is the pattern Microsoft recommends in their current documentation.

## My Take: The Options Pattern in Production

After years of building ASP.NET Core APIs, here are my strong opinions on the Options Pattern:

**Always use `ValidateOnStart`.** Configuration bugs should crash the deployment, not the user experience. I have seen too many production incidents caused by a missing key in `appsettings.json` that only surfaced when a specific code path was hit.

**Use `IValidateOptions<T>` over inline lambdas.** Yes, `.Validate(o => o.City != "")` is fewer lines. But when you have 5 validation rules and need to unit test them, a validator class is worth it.

**Do not overuse `IOptionsMonitor<T>`.** I see developers default to `IOptionsMonitor<T>` "just in case." If your config does not change at runtime, `IOptions<T>` is simpler and has zero overhead. Use `IOptionsMonitor<T>` when you actually need runtime reloading - feature flags, A/B test configuration, dynamic rate limits.

**Keep options classes focused.** One section, one class. Do not dump all your configuration into a single `AppSettings` god-object. Small, focused options classes are easier to validate, test, and reason about.

**Name your sections consistently.** I use the convention `[ClassName]` as the section name and add a `const string SectionName` to the class. This eliminates magic strings entirely.

## Troubleshooting Common Options Pattern Issues

Here are the issues I see most frequently when debugging Options Pattern problems:

**Options values are all null or default?** The most common cause is a section name mismatch. Your C# property `SectionName = "WeatherOptions"` must exactly match the JSON key in `appsettings.json`. Check capitalization - configuration binding is case-insensitive by default, but section names must match.

**OptionsValidationException on startup?** This means `.ValidateOnStart()` is working correctly and found invalid configuration. Check the exception message for the specific property and validation rule that failed. Fix the value in `appsettings.json` and restart.

**Injected IOptionsSnapshot into a singleton service?** `IOptionsSnapshot<T>` is scoped. In Development the app fails at startup with "Cannot consume scoped service ... from singleton"; in Production it starts and the singleton silently keeps its first snapshot forever. Use `IOptionsMonitor<T>` instead - it is singleton-safe and supports reloading. This is one of the most common DI lifetime mismatches in ASP.NET Core.

**Configuration changes not picked up?** If you are using `IOptions<T>`, that is expected - it reads once at startup. Switch to `IOptionsSnapshot<T>` (per-request reload) or `IOptionsMonitor<T>` (real-time reload). Also verify that `reloadOnChange: true` is set in your configuration builder (it is the default for `appsettings.json`).

**Named options returning empty values?** Make sure you are using the same name during registration and retrieval. `builder.Services.AddOptions<T>("MyName")` must match `optionsSnapshot.Get("MyName")`. Also remember that `IOptions<T>` does not support named options - you must use `IOptionsSnapshot<T>` or `IOptionsMonitor<T>`.

**PostConfigure not running?** `PostConfigure` runs after all `Configure` and `BindConfiguration` calls. If you register `PostConfigure` before `BindConfiguration`, the binding will overwrite your PostConfigure changes. Always register `PostConfigure` after binding.

## Key Takeaways

- **The Options Pattern binds configuration sections to strongly-typed C# classes**, providing type safety, validation, and DI support that raw `IConfiguration` cannot match.
- **Use `.ValidateDataAnnotations().ValidateOnStart()` on every options registration** to catch configuration errors at deployment time, not at runtime.
- **Default to `IOptions<T>`** for static configuration. Only use `IOptionsSnapshot<T>` or `IOptionsMonitor<T>` when you need runtime reloading.
- **Use `IValidateOptions<T>`** for complex, testable validation logic instead of inline lambdas.
- **Use `.AddOptions<T>().BindConfiguration()`** - it is the modern, chainable .NET 10 pattern that Microsoft recommends.

For more on building robust ASP.NET Core APIs, check out my guides on [dependency injection](/blog/dependency-injection-in-aspnet-core-explained/), [environment-based configuration](/blog/environment-based-configuration-aspnet-core/), and [structured logging with Serilog](/blog/structured-logging-with-serilog-in-aspnet-core/). If you are building a full API from scratch, my .NET Web API Zero to Hero course covers the Options Pattern as part of a complete learning path from your first endpoint to Docker deployment.

Happy Coding :)
