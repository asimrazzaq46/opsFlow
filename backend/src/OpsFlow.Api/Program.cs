using System.Text.Json.Serialization;
using OpsFlow.Api.Endpoints;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.Errors;
using OpsFlow.Api.Infrastructure.Initialization;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// UPDATED: Development ke bahar structured (JSON) logs
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(ProblemDetailsSetup.Configure); // UPDATED: har error body mein code + traceId
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddValidation();
builder.Services.AddOpsFlowOpenApi(); // UPDATED: Bearer security scheme + endpoint documentation

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration, builder.Environment);
// UPDATED: Development startup par migrate + seed (production mein kabhi register nahi hota)
builder.Services.AddDevelopmentInitialization(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // UPDATED: Swagger UI sirf Development mein, auth middleware se pehle taake fallback policy isay block na kare
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "OpsFlow API v1"));
}

// UPDATED: CORS (explicit origins) rate limiter se pehle
app.UseCors(ApiSecurityExtensions.CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // UPDATED: fallback policy ke baad OpenAPI document anonymous
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapOrganizationEndpoints();
// UPDATED: Milestone 2 endpoints
app.MapCustomerEndpoints();
app.MapTenantConfigEndpoints();
app.MapTicketEndpoints();

app.Run();

// Required for WebApplicationFactory in integration tests.
public partial class Program;
