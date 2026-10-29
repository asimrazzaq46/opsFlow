using OpsFlow.Api.Endpoints;
using OpsFlow.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // UPDATED: Swagger UI sirf Development mein, existing native OpenAPI document par
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "OpsFlow API v1"));
}

app.MapHealthEndpoints();

app.Run();

// Required for WebApplicationFactory in integration tests.
public partial class Program;
