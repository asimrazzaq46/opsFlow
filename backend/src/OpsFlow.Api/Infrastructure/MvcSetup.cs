using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace OpsFlow.Api.Infrastructure;

internal static class MvcSetup
{
    /// <summary>MVC controllers with the same JSON contract the API always had: string enums, application/json only, no CLR details in parse errors.</summary>
    public static IServiceCollection AddApiControllers(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter())
        ); // ProblemDetails writer

        services
            .AddControllers(o =>
            {
                // Drop MVC's text/json; output keeps application/*+json because ProblemDetails is application/problem+json.
                var input = o
                    .InputFormatters.OfType<SystemTextJsonInputFormatter>()
                    .Single()
                    .SupportedMediaTypes;
                input.Remove("text/json");
                input.Remove("application/*+json");
                o.OutputFormatters.OfType<SystemTextJsonOutputFormatter>()
                    .Single()
                    .SupportedMediaTypes.Remove("text/json");
                o.OutputFormatters.RemoveType<StringOutputFormatter>(); // no text/plain responses
            })
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.AllowInputFormatterExceptionMessages = false; // malformed JSON never leaks CLR type/path details to the client
            });
        return services;
    }
}
