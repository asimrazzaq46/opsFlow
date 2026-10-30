using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>The committed SDL (frontend/graphql/schema.graphql) feeds Angular code generation; this fails on drift. Regenerate with OPSFLOW_UPDATE_GRAPHQL_SCHEMA=1.</summary>
public class GraphQlSchemaTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string SchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "frontend", "graphql", "schema.graphql");
    }

    [Fact]
    public async Task Committed_graphql_schema_is_up_to_date()
    {
        var resolver = factory.Services.GetRequiredService<IRequestExecutorProvider>();
        var executor = await resolver.GetExecutorAsync();
        var sdl = executor.Schema.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        var path = SchemaPath();
        if (Environment.GetEnvironmentVariable("OPSFLOW_UPDATE_GRAPHQL_SCHEMA") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, sdl);
        }

        Assert.Equal(sdl, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
