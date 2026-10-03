using Goodmem.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RagChat.Api;
using RagChat.Api.Auth;
using RagChat.Api.Eval;
using RagChat.Api.GoodMem;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GoodMemOptions>()
    .BindConfiguration(GoodMemOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// GoodmemClient is thread-safe and holds its own HttpClient, so one instance for the app.
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<GoodMemOptions>>().Value;
    return new GoodmemClient(new GoodmemClientOptions
    {
        BaseUrl = options.BaseUrl,
        ApiKey = options.ApiKey,
        Timeout = options.Timeout
    });
});
builder.Services.AddSingleton<GoodMemSetup>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<EvalService>();

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapChatEndpoints();

app.Run();
