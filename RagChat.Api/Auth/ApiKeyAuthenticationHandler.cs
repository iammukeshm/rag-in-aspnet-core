using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RagChat.Api.Auth;

public sealed class ApiUser
{
    public string ApiKey { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}

// A deliberately small API key handler so the demo has real users. In production you would
// use your existing identity (JWT, cookies). What matters is that the user ID comes from here,
// never from the request body.
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var apiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var users = configuration.GetSection("Users").Get<List<ApiUser>>() ?? [];
        var user = users.FirstOrDefault(u => u.ApiKey == apiKey);
        if (user is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.UserId)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
