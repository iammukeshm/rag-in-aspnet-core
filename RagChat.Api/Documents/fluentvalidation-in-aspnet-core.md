# FluentValidation in ASP.NET Core .NET 10 - Beyond Data Annotations

Data Annotations are fine for a quick prototype - but the moment your validation rules involve cross-field checks, async database lookups, or conditional logic, they fall apart. I switched to FluentValidation years ago and haven't looked back. It keeps validation logic separate from your models, gives you full control over complex rules, and produces much cleaner code.

In this article, I will walk you through everything you need to know about FluentValidation in ASP.NET Core with .NET 10 - from basic setup to advanced patterns like endpoint filters for Minimal APIs and async validators. I will also share my take on when FluentValidation makes sense vs when Data Annotations or .NET 10's built-in validation is good enough.

## TL;DR - FluentValidation in ASP.NET Core .NET 10

**To use FluentValidation in ASP.NET Core .NET 10:** install the `FluentValidation` (12.x) and `FluentValidation.DependencyInjectionExtensions` packages, write a validator class that inherits `AbstractValidator<T>`, register it with `builder.Services.AddValidatorsFromAssemblyContaining<Program>()`, then validate either by injecting `IValidator<T>` and calling `ValidateAsync`, or by attaching a reusable `ValidationFilter<T>` endpoint filter to your Minimal API routes. The legacy `FluentValidation.AspNetCore` auto-validation package is deprecated and removed in v12 - do not use it.

**FluentValidation vs Data Annotations:** reach for FluentValidation on any production API where you need complex rules, async database checks, dependency injection, or clean unit testing. Data Annotations are fine for quick prototypes or Blazor forms where tight model coupling buys you free client-side validation. The full decision matrix is further down.

## Why Are Data Annotations Not Enough?

The go-to approach for model validation in .NET is Data Annotations - you slap attributes on your properties and call it a day. Worked with this before?

```csharp
public class UserRegistrationRequest
{
    [Required(ErrorMessage = "First name is required.")]
    public string? FirstName { get; set; }

    [Required(ErrorMessage = "Last name is required.")]
    public string? LastName { get; set; }

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Invalid email address.")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(6, ErrorMessage = "Password must be at least 6 characters.")]
    public string? Password { get; set; }

    [Required(ErrorMessage = "Please confirm your password.")]
    [Compare("Password", ErrorMessage = "Passwords do not match.")]
    public string? ConfirmPassword { get; set; }
}
```

This is fine for small projects and POCs. But once you start caring about [clean code](/blog/restful-api-best-practices-for-dotnet-developers/) and SOLID principles, mixing validation logic with your model classes starts to smell. Here is the problem:

- **Separation of concerns violation** - Validation logic is tightly coupled to your domain models. The model should describe the data, not how to validate it.
- **Limited expressiveness** - Try writing "if the country is US, then state is required" with attributes. You can't. Cross-field logic needs custom attributes that are painful to maintain.
- **No async support** - Need to check if an email is already taken against a database? Data Annotations have no built-in async path.
- **Testing friction** - You can't unit test validation rules in isolation without instantiating the model and running the full attribute pipeline.

So, what's the solution?

## What Is FluentValidation?

[FluentValidation](https://fluentvalidation.net/) is an open-source .NET library (380M+ NuGet downloads, [GitHub](https://github.com/FluentValidation/FluentValidation)) that lets you build strongly-typed validation rules using a fluent API. Instead of decorating your models with attributes, you write dedicated validator classes with expressive, chainable rules.

> FluentValidation provides a fluent interface for defining validation rules, allowing you to express complex validation logic clearly and concisely - including cross-field checks, async rules, and conditional logic that Data Annotations simply can't handle.

My rule of thumb: for a quick prototype or a model with 2-3 simple `[Required]` attributes, Data Annotations are perfectly fine. For anything more complex - and especially for production APIs - FluentValidation is the way to go.

## How to Set Up FluentValidation in .NET 10

For this walkthrough, I am using .NET 10 with a Minimal API project. You can follow along using Visual Studio 2026, VS Code, or any editor you prefer. I will test endpoints using [Scalar](/blog/aspnet-core-webapi-crud-with-entity-framework-core-full-course/#testing-with-scalar) (the modern replacement for Swagger UI).

### Installing the Packages

Install the required NuGet packages via the CLI:

```bash
dotnet add package FluentValidation --version 12.1.1
dotnet add package FluentValidation.DependencyInjectionExtensions --version 12.1.1
```

> **Breaking change in FluentValidation 12**: The `FluentValidation.AspNetCore` package is [deprecated and no longer maintained](https://github.com/FluentValidation/FluentValidation/issues/1959). The auto-validation pipeline it provided has been removed. You should use manual validation or endpoint filters instead - both approaches I cover below.

### The Request Model

For this demo, I will build a user registration API. Here is the request model - notice it has zero validation attributes. Clean.

```csharp
public class UserRegistrationRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
}
```

The goal is to validate this request whenever the registration endpoint is hit - ensuring valid names, a proper email address, and matching passwords. All without touching this model class.

## How to Write Validator Rules

Create a `Validators` folder and add a class named `UserRegistrationValidator`. This is where all the validation magic happens.

Start with a single rule that validates the Email property:

```csharp
public class UserRegistrationValidator : AbstractValidator<UserRegistrationRequest>
{
    public UserRegistrationValidator()
    {
        RuleFor(x => x.Email).EmailAddress();
    }
}
```

The validator inherits from `AbstractValidator<T>` where `T` is the model being validated. Rules are defined in the constructor using the `RuleFor` method - strongly typed, refactor-safe, and readable.

## How to Register Validators with Dependency Injection

FluentValidation integrates with ASP.NET Core's built-in [dependency injection](/blog/dependency-injection-in-aspnet-core-explained/) container. Open `Program.cs` and register your validators.

You can register a single validator manually:

```csharp
builder.Services.AddScoped<IValidator<UserRegistrationRequest>, UserRegistrationValidator>();
```

But for any real project with multiple validators, use assembly scanning - it discovers and registers all validators in one line:

```csharp
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
```

This scans the assembly containing `Program` for all classes that inherit from `AbstractValidator<T>` and registers them as `IValidator<T>` in the DI container. I always use this approach - no chance of forgetting to register a new validator.

## Manual Validation in Minimal API Endpoints

The recommended approach in FluentValidation 12 is manual validation. You inject `IValidator<T>` into your endpoint and call `ValidateAsync` explicitly.

```csharp
app.MapPost("/register", async (UserRegistrationRequest request, IValidator<UserRegistrationRequest> validator) =>
{
    var validationResult = await validator.ValidateAsync(request);
    if (!validationResult.IsValid)
    {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }
    // perform actual service call to register the user
    return Results.Accepted();
});
```

I inject `IValidator<UserRegistrationRequest>` directly into the endpoint delegate. The validator runs asynchronously, and if validation fails, the endpoint returns a standard [Problem Details](/blog/problem-details-in-aspnet-core/) response per [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) - exactly what REST APIs should return for validation errors.

Build and run your .NET 10 API, open Scalar at `/scalar`, and send a POST request:

```json
{
  "firstName": "string",
  "lastName": "string",
  "email": "string",
  "password": "string",
  "confirmPassword": "string"
}
```

Since `"string"` is not a valid email address, the response comes back as:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Email": ["'Email' is not a valid email address."]
  }
}
```

A clean 400 Bad Request with validation errors in the standard Problem Details format. Pass a valid email and the error disappears. Get the point?

## Endpoint Filters: Auto-Validation for Minimal APIs

Manual validation works, but having to write those same 5 lines of validation code in every single endpoint gets repetitive fast. The old `FluentValidation.AspNetCore` package solved this with auto-validation in the MVC pipeline - but that package is deprecated in v12.

The modern replacement? **Endpoint filters**. This is a reusable [filter](/blog/filters-in-aspnet-core/) that auto-validates any request before your endpoint handler runs.

```csharp
public class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var validator = context.HttpContext.RequestServices
            .GetService<IValidator<T>>();

        if (validator is null)
        {
            return await next(context);
        }

        var model = context.Arguments
            .OfType<T>()
            .FirstOrDefault();

        if (model is null)
        {
            return Results.Problem("Request body is required.", statusCode: 400);
        }

        var validationResult = await validator.ValidateAsync(model);

        if (!validationResult.IsValid)
        {
            return Results.ValidationProblem(validationResult.ToDictionary());
        }

        return await next(context);
    }
}
```

This generic filter resolves `IValidator<T>` from the DI container, finds the matching argument from the endpoint's parameters, validates it, and short-circuits with a Problem Details response if validation fails. If no validator is registered for the type, it passes through without blocking.

Now your endpoints become dead simple:

```csharp
app.MapPost("/register-with-filter", (UserRegistrationRequest request) =>
{
    // validation already handled by the filter
    return Results.Accepted();
})
.AddEndpointFilter<ValidationFilter<UserRegistrationRequest>>();
```

No injected validator. No manual `ValidateAsync` call. The filter handles everything. Isn't that cool?

My take: for APIs with more than 3-4 endpoints, always use the endpoint filter approach. The small upfront investment pays off massively in reduced boilerplate and consistent validation behavior across your entire API. Endpoint filters are a [first-class Minimal API feature](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/min-api-filters) - they run in the same pipeline as route handlers with near-zero overhead.

## Exploring FluentValidation's Built-in Features

Now that the infrastructure is in place, let me show you the power of FluentValidation's rule system.

### Custom Validation Messages

Override the default error message with `WithMessage()`:

```csharp
RuleFor(x => x.Email).EmailAddress().WithMessage("{PropertyName} is invalid! Please check!");
```

`{PropertyName}` is a built-in placeholder that resolves to the property name. FluentValidation has several of these - check the [official docs](https://docs.fluentvalidation.net/en/latest/built-in-validators.html) for the full list.

### Built-in Validators and Chaining

FluentValidation ships with a rich set of built-in validators. Here is a more complete validator:

```csharp
public class UserRegistrationValidator : AbstractValidator<UserRegistrationRequest>
{
    public UserRegistrationValidator()
    {
        RuleFor(x => x.FirstName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MinimumLength(4)
            .Must(IsValidName).WithMessage("{PropertyName} should be all letters.");

        RuleFor(x => x.LastName)
            .NotEmpty()
            .MaximumLength(10);

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .WithMessage("{PropertyName} is not a valid email address.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(6);

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.Password)
            .WithMessage("Passwords do not match.");
    }

    private static bool IsValidName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && name.All(char.IsLetter);
    }
}
```

Let me break down what each rule does:

1. **FirstName** - Must not be empty, must be at least 4 characters, and must contain only letters (using a custom `Must` validator).
2. **LastName** - Must not be empty, maximum 10 characters.
3. **Email** - Must not be empty and must be a valid email address.
4. **Password** - Must not be empty and at least 6 characters.
5. **ConfirmPassword** - Must match the `Password` field.

Notice how multiple validators are chained on a single property - `NotEmpty().MinimumLength(4).Must(IsValidName)`. This is what "fluent" means. Each validator runs in order, and you get separate error messages for each failure.

### Overriding the Property Name

Want to change how the property name appears in error messages? Use `WithName`:

```csharp
RuleFor(x => x.Email).EmailAddress().WithName("MailID").WithMessage("{PropertyName} is invalid! Please check!");
```

The response now shows:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Email": ["MailID is invalid! Please check!"]
  }
}
```

### Stop on First Failure with CascadeMode

By default, FluentValidation runs all validators on a property even if an earlier one already failed. For the `FirstName` rule with `NotEmpty().MinimumLength(4)`, passing an empty string returns two errors:

```json
{
  "errors": {
    "FirstName": [
      "'First Name' must not be empty.",
      "The length of 'First Name' must be at least 4 characters. You entered 0 characters."
    ]
  }
}
```

That second error is noise - if the name is empty, the length check is pointless. Fix it with `CascadeMode.Stop`:

```csharp
RuleFor(x => x.FirstName).Cascade(CascadeMode.Stop).NotEmpty().MinimumLength(4);
```

Now validation stops at the first failure for that property. I use `CascadeMode.Stop` on almost every rule - it gives users one actionable error at a time instead of a wall of messages.

### Custom Validators with Must

The `Must` validator is your escape hatch for any business rule that built-in validators can't express. In the example above, I used it to ensure names contain only letters:

```csharp
private static bool IsValidName(string? name)
{
    return !string.IsNullOrWhiteSpace(name) && name.All(char.IsLetter);
}
```

```csharp
RuleFor(x => x.FirstName)
    .Cascade(CascadeMode.Stop)
    .NotEmpty()
    .MinimumLength(4)
    .Must(IsValidName).WithMessage("{PropertyName} should be all letters.");
```

If someone passes `"John123"` as their first name, they get:

```json
{
  "errors": {
    "FirstName": ["First Name should be all letters."]
  }
}
```

`Must` accepts any `Func<T, bool>` - so you can wire up any logic you need.

## How to Use Async Validators for Database-Backed Rules

One of FluentValidation's killer features is `MustAsync` - it lets you write validation rules that hit a database, call an API, or do any async work. This is something Data Annotations simply cannot do.

Here is a common scenario: checking if an email is already registered.

```csharp
public class UserRegistrationValidator : AbstractValidator<UserRegistrationRequest>
{
    public UserRegistrationValidator(AppDbContext dbContext)
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MustAsync(async (email, cancellationToken) =>
            {
                return !await dbContext.Users
                    .AnyAsync(u => u.Email == email, cancellationToken);
            })
            .WithMessage("This email is already registered.");
    }
}
```

Because the validator is resolved from the DI container, you can inject any service - a `DbContext`, a repository, an HTTP client. The async rule runs during `ValidateAsync` and produces the same clean error response.

> **Important**: FluentValidation 11+ enforces strict async rules. If your validator contains any `MustAsync` or `CustomAsync` rule and you call `Validate()` instead of `ValidateAsync()`, it throws `AsyncValidatorInvokedSynchronouslyException`. Always use `ValidateAsync()` when async rules are present. Trust me, this will save you a debugging headache.

## Enhancing with MediatR Pipeline Behavior

If you are using [MediatR](/blog/cqrs-and-mediatr-in-aspnet-core/) in your API, you can take validation to the next level. Instead of manually calling validators or attaching endpoint filters, a MediatR Pipeline Behavior can automatically validate every command and query that flows through the [middleware pipeline](/blog/middlewares-in-aspnet-core/).

This means zero validation code in your endpoints - the pipeline behavior intercepts the request, validates it, and throws a validation exception if it fails. Your [global exception handler](/blog/global-exception-handling-in-aspnet-core/) converts the exception into a Problem Details response.

I covered this pattern in detail here: [Validation with MediatR Pipeline Behavior and FluentValidation](/blog/validation-with-mediatr-pipeline-behavior-and-fluentvalidation/). If you are building anything beyond a simple CRUD API, I highly recommend this approach.

## My Take: FluentValidation vs Data Annotations vs .NET 10 Built-in Validation

This is the question I get asked most often. Here is my honest take, based on years of building production .NET APIs.

| Criteria | Data Annotations | FluentValidation | .NET 10 Built-in |
|----------|-----------------|------------------|-------------------|
| **Setup effort** | Lowest - just add attributes | Medium - separate validator classes | Low - framework-level |
| **Complex rules** | Poor - custom attributes are painful | Excellent - fluent API, cross-field, conditional | Basic - limited to simple checks |
| **Async validation** | Not supported | Full support (`MustAsync`) | Not supported |
| **Separation of concerns** | Violates - logic in models | Clean - dedicated validator classes | Depends on implementation |
| **Unit testing** | Awkward - need full model pipeline | Easy - instantiate validator, call `Validate` | Varies |
| **Minimal API support** | Limited | Full (manual + endpoint filters) | Native |
| **DI integration** | None | Full - inject services into validators | Framework-level |
| **Ecosystem** | Built-in | [380M+ NuGet downloads](https://www.nuget.org/packages/FluentValidation) | New in .NET 10 |

**My verdict**: For production APIs, FluentValidation is the clear winner. The separation of concerns alone justifies it - but the async support, DI integration, and testability make it a no-brainer for anything beyond a prototype.

I use Data Annotations only for quick demos or Blazor forms where the tight model coupling actually helps (you get client-side validation for free). For API-first development? FluentValidation, every time.

## Troubleshooting Common Issues

### AsyncValidatorInvokedSynchronouslyException

**Cause**: You called `validator.Validate()` (synchronous) on a validator that contains `MustAsync` or `CustomAsync` rules. FluentValidation 11+ throws this exception to prevent deadlocks.

**Fix**: Always use `await validator.ValidateAsync(model)`. If you are not sure whether a validator has async rules, just use `ValidateAsync` everywhere - it works for sync rules too.

### Validators Not Being Discovered by DI

**Cause**: You registered validators with `AddValidatorsFromAssemblyContaining<T>()` but your validators are in a different assembly.

**Fix**: Pass a type from the assembly that contains your validators. If they are in a `Validators` project, use `AddValidatorsFromAssemblyContaining<UserRegistrationValidator>()`. Or use `AddValidatorsFromAssembly(typeof(UserRegistrationValidator).Assembly)`.

### FluentValidation.AspNetCore Deprecated Errors

**Cause**: You upgraded to FluentValidation 12 but still reference the `FluentValidation.AspNetCore` package, which is deprecated and no longer published.

**Fix**: Remove the package. Use manual validation or the endpoint filter approach I showed above. The two packages you need are `FluentValidation` and `FluentValidation.DependencyInjectionExtensions`.

### Validation Not Triggering on Minimal API Endpoints

**Cause**: Minimal APIs do not have the MVC model binding pipeline, so validators do not run automatically even if registered.

**Fix**: Either inject `IValidator<T>` and call `ValidateAsync` manually, or attach the `ValidationFilter<T>` endpoint filter. There is no automatic validation for Minimal APIs - you must opt in explicitly.

### InjectValidator Removed in FluentValidation 12

**Cause**: The `InjectValidator()` method was part of the auto-validation pipeline in `FluentValidation.AspNetCore`, which has been removed in v12.

**Fix**: Use `SetValidator(new ChildValidator())` for inline child validators, or let DI handle it by registering child validators separately and injecting them via the constructor.

## Summary

FluentValidation has been my go-to validation library for years, and version 12 with .NET 10 is the best it has ever been. The deprecation of `FluentValidation.AspNetCore` was the right call - the endpoint filter approach gives you the same auto-validation convenience with more control and better alignment with the [Minimal API](/blog/minimal-apis-aspnet-core/) model.

Here is what I want you to take away from this article:

1. **Separate validation from models** - Use dedicated validator classes, not attributes on your DTOs.
2. **Use endpoint filters** for auto-validation in Minimal APIs - it's the modern replacement for the deprecated auto-validation pipeline.
3. **Embrace async validation** with `MustAsync` - check database uniqueness, call external services, validate against real data.
4. **Always use `ValidateAsync`** - even for sync-only validators. It future-proofs your code.
5. **Consider MediatR Pipeline Behavior** for [CQRS-based architectures](/blog/cqrs-and-mediatr-in-aspnet-core/) - zero validation code in your endpoints.

You can find the complete source code for this article in the [GitHub repository](https://github.com/codewithmukesh/dotnet-webapi-zero-to-hero-course/tree/main/modules/01-getting-started/fluentvalidation-in-aspnet-core). This article is part of the .NET Web API Zero to Hero Course, where I cover validation, [configuration with the Options Pattern](/blog/options-pattern-in-aspnet-core/), [global exception handling](/blog/global-exception-handling-in-aspnet-core/), and much more.

Happy Coding :)
