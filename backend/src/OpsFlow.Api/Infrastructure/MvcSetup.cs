using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace OpsFlow.Api.Infrastructure;

internal static class MvcSetup
{
    /// <summary>MVC controllers with the same JSON contract the API always had: string enums, application/json only, no CLR details in parse errors.</summary>
    public static IServiceCollection AddApiControllers(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter())); // ProblemDetails writer

        services.AddControllers(o =>
            {
                // MVC ka text/json hata diya; output mein application/*+json zaroori hai kyunke ProblemDetails application/problem+json hai
                var input = o.InputFormatters.OfType<SystemTextJsonInputFormatter>().Single().SupportedMediaTypes;
                input.Remove("text/json");
                input.Remove("application/*+json");
                o.OutputFormatters.OfType<SystemTextJsonOutputFormatter>().Single().SupportedMediaTypes.Remove("text/json");
                o.OutputFormatters.RemoveType<StringOutputFormatter>(); // text/plain response media type nahi chahiye
            })
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.AllowInputFormatterExceptionMessages = false; // malformed JSON par CLR type/path ki tafseel client ko nahi jati
            });
        return services;
    }
}
