using OpsFlow.Api.GraphQL;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.Background;
using OpsFlow.Api.Infrastructure.Errors;
using OpsFlow.Api.Infrastructure.Initialization;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure;
using OpsFlow.Realtime;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

builder.Services.AddApiControllers();
builder.Services.AddProblemDetails(ProblemDetailsSetup.Configure); // every error body carries code + traceId
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpsFlowOpenApi(); // Bearer security scheme + endpoint documentation

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
// Redis backplane only when configured; otherwise in-process.
builder.Services.AddRealtime(o => builder.Configuration.GetSection(RealtimeOptions.SectionName).Bind(o));
builder.Services.AddApiSecurity(builder.Configuration, builder.Environment);
builder.Services.AddOpsFlowGraphQl(builder.Environment); // GraphQL read layer
builder.Services.AddOpsFlowBackground(builder.Configuration); // Hangfire worker runs only when Background:ServerEnabled is set
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

app.UseOpsFlowHangfireDashboard(); // off by default; loopback-only, explicit opt-in in production
app.MapControllers();
app.MapOpsFlowGraphQl(app.Environment); // /api/v1/tenants/{tenantId}/graphql (read-only, same membership policy as REST)
app.MapRealtimeHub(); // authenticated SignalR hub (/hubs/realtime)

app.Run();

// Required for WebApplicationFactory in integration tests.
public partial class Program;
