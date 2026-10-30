using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.Errors;
using OpsFlow.Api.Infrastructure.Initialization;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

builder.Services.AddApiControllers(); // UPDATED: Minimal APIs ki jagah MVC Controllers
builder.Services.AddProblemDetails(ProblemDetailsSetup.Configure); // UPDATED: every error body have code + traceId
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpsFlowOpenApi(); // Bearer security scheme + endpoint documentation

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration, builder.Environment);
builder.Services.AddDevelopmentInitialization(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "OpsFlow API v1"));
}

app.UseCors(ApiSecurityExtensions.CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapControllers(); // UPDATED: saare endpoints Controllers/ se

app.Run();

// Required for WebApplicationFactory in integration tests.
public partial class Program;
