# ASP.NET Core Configuration - appsettings, Env Vars & User Secrets

Every real-world application needs to behave differently depending on where it runs. Your local machine uses a different database than production. API keys change between environments. Logging verbosity cranks up during development but stays quiet in production.

ASP.NET Core handles this through a layered configuration system that's both powerful and predictable. Configuration values can come from JSON files, environment variables, command-line arguments, user secrets, and more - all merged together with clear precedence rules.

In this article, we'll cover how configuration works in .NET 10, how to structure your `appsettings.json` files, how environment variables override settings, how `launchSettings.json` works (and why it's misunderstood), and how to use User Secrets to keep sensitive data out of source control. We'll also cover the most common mistakes developers make and how to avoid them.

> This article is part of my .NET Web API Zero to Hero course, where I cover everything you need to know to build efficient and scalable APIs. Configuration is foundational - once you understand how it works, everything from connection strings to feature flags becomes straightforward.

Let's get into it.

## How Configuration Works in ASP.NET Core

ASP.NET Core uses a layered configuration system. Multiple configuration sources are loaded in a specific order, and later sources **override** earlier ones. This is the foundation of environment-based configuration.

When your application starts, the default `WebApplication.CreateBuilder(args)` method loads configuration from these sources, in this order:

1. `appsettings.json`
2. `appsettings.{Environment}.json` (e.g., `appsettings.Development.json`)
3. User Secrets (only in Development environment)
4. Environment variables
5. Command-line arguments

Each subsequent source overrides values from previous sources. This means:

- Values in `appsettings.Development.json` override `appsettings.json`
- Environment variables override both JSON files
- Command-line arguments have the highest priority

This layering is intentional. You define sensible defaults in `appsettings.json`, override them for specific environments, and use environment variables or secrets for sensitive data that should never be committed to source control.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configuration is already loaded at this point
// You can access it via builder.Configuration
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
```

The `IConfiguration` interface is available throughout your application via [dependency injection](/blog/dependency-injection-in-aspnet-core-explained/). You can inject it anywhere you need to read configuration values.

## Understanding the ASPNETCORE_ENVIRONMENT Variable

The `ASPNETCORE_ENVIRONMENT` environment variable controls which environment your application runs in. ASP.NET Core recognizes three standard values:

- `Development`
- `Staging`
- `Production`

When this variable is set to `Development`, ASP.NET Core:

1. Loads `appsettings.Development.json` after `appsettings.json`
2. Enables User Secrets
3. Shows detailed error pages
4. Enables developer exception pages

When set to `Production` (or not set at all - Production is the default):

1. Loads `appsettings.Production.json`
2. Disables User Secrets
3. Shows generic error pages
4. Optimizes for performance

You can also use custom environment names like `QA`, `UAT`, or `PreProduction`. Just create the corresponding `appsettings.{EnvironmentName}.json` file.

```csharp
// Check the current environment in code
if (builder.Environment.IsDevelopment())
{
    // Development-specific setup
}

if (builder.Environment.IsProduction())
{
    // Production-specific setup
}

// Or check for custom environments
if (builder.Environment.IsEnvironment("Staging"))
{
    // Staging-specific setup
}
```

## Working with appsettings.json

The `appsettings.json` file is the primary configuration source for most applications. It's a JSON file at the root of your project that stores configuration values in a hierarchical structure.

### Basic Structure

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=MyApp;Trusted_Connection=true;"
  },
  "AppSettings": {
    "ApplicationName": "My Awesome API",
    "MaxRetryAttempts": 3,
    "EnableFeatureX": true
  }
}
```

### Accessing Configuration Values

There are multiple ways to access configuration values. The simplest is using `IConfiguration` directly:

```csharp
public class MyService(IConfiguration configuration)
{
    public void DoSomething()
    {
        // Access a simple value
        var appName = configuration["AppSettings:ApplicationName"];

        // Access nested values using colon separator
        var logLevel = configuration["Logging:LogLevel:Default"];

        // Get typed values
        var maxRetries = configuration.GetValue<int>("AppSettings:MaxRetryAttempts");
        var enableFeature = configuration.GetValue<bool>("AppSettings:EnableFeatureX");

        // Get connection strings (special helper method)
        var connectionString = configuration.GetConnectionString("DefaultConnection");
    }
}
```

The colon (`:`) is the separator for nested configuration keys. `Logging:LogLevel:Default` navigates to `Logging` → `LogLevel` → `Default`.

### Environment-Specific Override Files

Create environment-specific files to override base settings:

**appsettings.json** (base defaults):
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=prodserver;Database=MyApp;..."
  },
  "FeatureFlags": {
    "EnableDebugEndpoints": false
  }
}
```

**appsettings.Development.json** (development overrides):
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.EntityFrameworkCore": "Information"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=MyApp_Dev;Trusted_Connection=true;"
  },
  "FeatureFlags": {
    "EnableDebugEndpoints": true
  }
}
```

**appsettings.Staging.json** (staging overrides):
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=stagingserver;Database=MyApp_Staging;..."
  }
}
```

Only include the values you want to override. The configuration system merges them intelligently.

## Environment Variables – The Production Standard

Environment variables are the **preferred way** to configure applications in production. They're secure (not committed to source control), easy to change without redeployment, and work consistently across all hosting platforms.

### The Double-Underscore Convention

Here's something that trips up many developers: **ASP.NET Core uses double underscores (`__`) to represent the colon (`:`) separator in environment variable names**.

To override `ConnectionStrings:DefaultConnection`, set:

```bash
ConnectionStrings__DefaultConnection=Server=prodserver;Database=MyApp;...
```

To override `Logging:LogLevel:Default`:

```bash
Logging__LogLevel__Default=Error
```

This convention exists because colons are not valid in environment variable names on all platforms (especially Linux).

### Setting Environment Variables

**Windows (PowerShell):**
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ConnectionStrings__DefaultConnection = "Server=prodserver;..."
```

**Windows (Command Prompt):**
```cmd
set ASPNETCORE_ENVIRONMENT=Production
set ConnectionStrings__DefaultConnection=Server=prodserver;...
```

**Linux/macOS:**
```bash
export ASPNETCORE_ENVIRONMENT=Production
export ConnectionStrings__DefaultConnection="Server=prodserver;..."
```

**Docker:**
```dockerfile
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ConnectionStrings__DefaultConnection="Server=prodserver;..."
```

Or in `docker-compose.yml`:
```yaml
services:
  api:
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__DefaultConnection=Server=prodserver;...
```

### Why Environment Variables Override JSON Files

Environment variables take precedence over `appsettings.json` files. This is intentional and powerful:

1. **Security**: Sensitive values like connection strings and API keys should never be in source control
2. **Flexibility**: Change configuration without rebuilding or redeploying
3. **Platform compatibility**: Works identically across Docker, Kubernetes, Azure, AWS, and on-premises

A common pattern is:

- `appsettings.json` contains non-sensitive defaults and structure
- `appsettings.Development.json` contains development-specific values
- Environment variables provide all sensitive and environment-specific values in production

## Launch Profiles (launchSettings.json) – Local Development Only

The `launchSettings.json` file in the `Properties` folder configures how your application launches during local development. It's commonly misunderstood, so let's clear things up.

**Critical point: `launchSettings.json` is NOT deployed. It only affects local development with Visual Studio, VS Code, Rider, or `dotnet run`.**

### Understanding Launch Profiles

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "launchUrl": "scalar/v1",
      "applicationUrl": "http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    },
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "launchUrl": "scalar/v1",
      "applicationUrl": "https://localhost:5001;http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    },
    "Production-Local": {
      "commandName": "Project",
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Production",
        "ConnectionStrings__DefaultConnection": "Server=localhost;Database=MyApp_Prod;..."
      }
    }
  }
}
```

### Key Properties Explained

| Property | Purpose |
|----------|---------|
| `commandName` | How to run the app. `Project` runs directly, `IISExpress` uses IIS Express |
| `applicationUrl` | The URLs your app listens on locally |
| `environmentVariables` | Environment variables set for this profile |
| `launchBrowser` | Whether to open the browser automatically |
| `launchUrl` | The relative URL to open in the browser |

The `launchUrl` here points at `scalar/v1` because .NET 10 templates ship with [Scalar instead of Swagger UI](/blog/dotnet-swagger-alternatives-openapi/) for API docs.

### Selecting a Launch Profile

**dotnet CLI:**
```bash
dotnet run --launch-profile "https"
dotnet run --launch-profile "Production-Local"
```

**Visual Studio:**
Use the dropdown next to the Start button to select a profile.

### Common Misconceptions

1. **"I put my production connection string in launchSettings.json"** - Never do this. `launchSettings.json` is for local development only and should be in `.gitignore` if it contains any sensitive data.
2. **"My app works locally but fails in production"** - If you're relying on environment variables set in `launchSettings.json`, they won't exist in production. Ensure your production environment has all required variables.
3. **"I need launchSettings.json in my Docker image"** - No, you don't. Set environment variables in your Dockerfile or container orchestration (Kubernetes, ECS, etc.).

## User Secrets – Keeping Sensitive Data Out of Source Control

User Secrets is a development-time feature that stores sensitive configuration outside your project folder. It's perfect for API keys, connection strings, and other secrets during local development.

### How User Secrets Work

Secrets are stored in a JSON file in your user profile directory:

- **Windows:** `%APPDATA%\Microsoft\UserSecrets\<user_secrets_id>\secrets.json`
- **macOS/Linux:** `~/.microsoft/usersecrets/<user_secrets_id>/secrets.json`

Each project has a unique `UserSecretsId` in its `.csproj` file, ensuring secrets are project-specific.

### Setting Up User Secrets

**Step 1: Initialize User Secrets**

```bash
dotnet user-secrets init
```

This adds a `UserSecretsId` to your `.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <UserSecretsId>your-unique-guid-here</UserSecretsId>
</PropertyGroup>
```

**Step 2: Add Secrets**

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=MyApp;Password=SuperSecret123!"
dotnet user-secrets set "ExternalApi:ApiKey" "sk-1234567890abcdef"
dotnet user-secrets set "Smtp:Password" "email-password-here"
```

**Step 3: View Secrets**

```bash
dotnet user-secrets list
```

**Step 4: Remove Secrets**

```bash
dotnet user-secrets remove "ConnectionStrings:DefaultConnection"
dotnet user-secrets clear  # Remove all secrets
```

### Accessing User Secrets in Code

No code changes needed. User Secrets are automatically loaded in the Development environment:

```csharp
var builder = WebApplication.CreateBuilder(args);

// User Secrets are already loaded if ASPNETCORE_ENVIRONMENT=Development
var apiKey = builder.Configuration["ExternalApi:ApiKey"];
```

### User Secrets Best Practices

1. **Development only**: User Secrets only work when `ASPNETCORE_ENVIRONMENT=Development`. Use environment variables or a proper secrets manager like [AWS Secrets Manager](/blog/secrets-in-aspnet-core-with-aws-secrets-manager/) or Azure Key Vault in production. For non-sensitive configuration that you still want centralized, [AWS Systems Manager Parameter Store](/blog/aws-parameter-store-dotnet/) is a free option.
2. **Don't commit the secrets file**: The secrets file is outside your project by design. Never copy it into your repository.
3. **Team sharing**: User Secrets are per-machine. Each developer needs to set up their own secrets. Document which secrets are required in your README.
4. **Use for local overrides**: Perfect for overriding `appsettings.Development.json` with machine-specific values like local database credentials.

## The Options Pattern – Strongly Typed Configuration

While `IConfiguration` works, accessing configuration via string keys is error-prone. The Options Pattern provides strongly typed access to configuration sections.

```csharp
public class SmtpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; }
}
```

**appsettings.json:**
```json
{
  "SmtpSettings": {
    "Host": "smtp.example.com",
    "Port": 587,
    "Username": "noreply@example.com",
    "Password": "",
    "EnableSsl": true
  }
}
```

**Registration:**
```csharp
builder.Services.Configure<SmtpSettings>(
    builder.Configuration.GetSection("SmtpSettings"));
```

**Usage:**
```csharp
public class EmailService(IOptions<SmtpSettings> options)
{
    private readonly SmtpSettings _settings = options.Value;

    public void SendEmail()
    {
        // Use _settings.Host, _settings.Port, etc.
    }
}
```

## Configuration Precedence – The Complete Picture

Let's consolidate the configuration precedence order. Later sources override earlier ones:

1. **appsettings.json** - Base defaults
2. `appsettings.{Environment}.json` - Environment-specific overrides
3. **User Secrets** - Development-only secrets (only when `ASPNETCORE_ENVIRONMENT=Development`)
4. **Environment Variables** - Runtime overrides
5. **Command-line Arguments** - Highest priority

### Visual Example

Given these sources:

**appsettings.json:**
```json
{
  "ApiSettings": {
    "BaseUrl": "https://api.example.com",
    "Timeout": 30,
    "RetryCount": 3
  }
}
```

**appsettings.Development.json:**
```json
{
  "ApiSettings": {
    "BaseUrl": "https://dev-api.example.com"
  }
}
```

**Environment Variable:**
```bash
ApiSettings__Timeout=60
```

**Final Merged Configuration (in Development):**
```json
{
  "ApiSettings": {
    "BaseUrl": "https://dev-api.example.com",  // From appsettings.Development.json
    "Timeout": 60,                              // From environment variable
    "RetryCount": 3                             // From appsettings.json (not overridden)
  }
}
```

## Common Mistakes and How to Avoid Them

### Mistake 1: Hardcoding Connection Strings

**Bad:**
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=prodserver;Database=MyApp;User=admin;Password=P@ssw0rd!"
  }
}
```

**Better:** Use User Secrets for development and environment variables for production:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": ""
  }
}
```

Set the actual value via:
```bash
# Development (User Secrets)
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;..."

# Production (Environment Variable)
ConnectionStrings__DefaultConnection=Server=prodserver;...
```

### Mistake 2: Using Single Underscores in Environment Variables

**Wrong:**
```bash
ConnectionStrings_DefaultConnection=...  # Single underscore - won't work!
```

**Correct:**
```bash
ConnectionStrings__DefaultConnection=...  # Double underscore
```

### Mistake 3: Forgetting ASPNETCORE_ENVIRONMENT in Production

If `ASPNETCORE_ENVIRONMENT` isn't set, ASP.NET Core defaults to `Production`. This is usually fine, but it means:

- `appsettings.Development.json` won't be loaded
- User Secrets won't be available
- Developer exception pages are disabled

Always explicitly set the environment in production to make configuration predictable:

```bash
ASPNETCORE_ENVIRONMENT=Production
```

### Mistake 4: Committing launchSettings.json with Secrets

If your `launchSettings.json` contains sensitive environment variables for testing, add it to `.gitignore`:

```txt
**/Properties/launchSettings.json
```

Or better: never put secrets in `launchSettings.json`. Use User Secrets instead.

### Mistake 5: Not Validating Configuration at Startup

A missing or malformed configuration value can crash your app at runtime. Validate required settings at startup:

```csharp
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException(
        "DefaultConnection connection string is required. " +
        "Set it via environment variable: ConnectionStrings__DefaultConnection");
}
```

Or use Options validation:

```csharp
builder.Services
    .AddOptions<SmtpSettings>()
    .Bind(builder.Configuration.GetSection("SmtpSettings"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

### Mistake 6: Logging Sensitive Configuration Values

**Never do this:**
```csharp
_logger.LogInformation("Connection string: {ConnectionString}", connectionString);
```

If you need to verify configuration is loaded, log non-sensitive indicators:

```csharp
_logger.LogInformation("Database server: {Server}",
    new SqlConnectionStringBuilder(connectionString).DataSource);
```

## CI/CD and Deployment Patterns

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

# Set default environment
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MyApp.Api/MyApp.Api.csproj", "MyApp.Api/"]
RUN dotnet restore "MyApp.Api/MyApp.Api.csproj"
COPY . .
RUN dotnet build "MyApp.Api/MyApp.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MyApp.Api/MyApp.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MyApp.Api.dll"]
```

If multi-stage Dockerfiles are new to you, my [Docker guide for .NET developers](/blog/docker-guide-for-dotnet-developers/) walks through the whole build from scratch.

Set environment variables when running the container:

```bash
docker run -d \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__DefaultConnection="Server=dbserver;..." \
  -e ExternalApi__ApiKey="sk-production-key" \
  -p 8080:8080 \
  myapp:latest
```

### GitHub Actions

Pulling secrets from `${{ secrets.* }}` into environment variables is the standard pattern. For a full pipeline that builds, tests, and deploys, see [deploying a .NET Web API to Amazon ECS with GitHub Actions](/blog/github-actions-deploy-dotnet-webapi-to-amazon-ecs/).

```yaml
- name: Deploy to Production
  env:
    ASPNETCORE_ENVIRONMENT: Production
    ConnectionStrings__DefaultConnection: ${{ secrets.DB_CONNECTION_STRING }}
    ExternalApi__ApiKey: ${{ secrets.API_KEY }}
  run: |
    # Deployment script here
```

### Azure App Service

Set configuration in the Azure Portal under **Configuration > Application settings**, or via Azure CLI:

```bash
az webapp config appsettings set \
  --name myapp \
  --resource-group mygroup \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__DefaultConnection="Server=tcp:myserver.database.windows.net;..."
```

## Quick Reference

| What You Want | How To Do It |
|---------------|--------------|
| Set base defaults | `appsettings.json` |
| Override for development | `appsettings.Development.json` |
| Store local secrets | `dotnet user-secrets set "Key" "Value"` |
| Override in production | Environment variable: `Key__NestedKey=value` |
| Check current environment | `builder.Environment.IsDevelopment()` |
| Set environment | `ASPNETCORE_ENVIRONMENT=Development` |
| Access configuration | `builder.Configuration["Section:Key"]` |
| Strongly typed config | Options Pattern with `IOptions<T>` |

## Summary

Configuration in ASP.NET Core is designed to be layered, predictable, and secure:

1. **appsettings.json** provides base defaults that apply everywhere
2. `appsettings.{Environment}.json` overrides values for specific environments
3. **User Secrets** keeps sensitive data out of source control during development
4. **Environment Variables** are the production standard for configuration
5. **launchSettings.json** is local-only and never deployed

The key insight is the precedence chain: each layer can override the previous one, with environment variables and command-line arguments having the final say. This lets you commit sensible defaults while keeping secrets secure.

Master this configuration system, and you'll never struggle with environment-specific settings again. Your apps will work identically across developer machines, CI pipelines, and production servers - each with their appropriate configuration.
