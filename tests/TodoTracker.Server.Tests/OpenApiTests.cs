using System.Text.Json.Nodes;

namespace TodoTracker.Server.Tests;

/// <summary>
/// The REST API description that GPT Actions, Copilot Studio and other HTTP-based AI tools import. It is a curated,
/// safe subset: everything an assistant needs to read and organize tasks, nothing destructive or about sign-in.
/// </summary>
public sealed class OpenApiTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task OpenApi_document_is_public_and_describes_the_task_api()
    {
        var doc = await Get("/openapi/v1.json");

        Assert.StartsWith("3.1", doc["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Todo Tracker", doc["info"]!["title"]!.GetValue<string>());
        var operations = Operations(doc);
        Assert.Contains(("get", "/api/dashboard", "getDashboard"), operations);
        Assert.Contains(("post", "/api/items", "createTask"), operations);
        Assert.Contains(("post", "/api/items/{id}/complete", "completeTask"), operations);
        Assert.Contains(("get", "/api/search", "searchTasks"), operations);
        Assert.Contains(("post", "/api/items/{id}/history/{version}/restore", "restoreTaskVersion"), operations);
    }

    [Fact]
    public async Task OpenApi_leaves_out_destructive_and_sign_in_endpoints()
    {
        var operations = Operations(await Get("/openapi/v1.json"));

        Assert.DoesNotContain(operations, o => o.Method == "delete");
        Assert.DoesNotContain(operations, o => o.Path is "/api/login" or "/api/launch" or "/api/auth" or "/api/connection" or "/api/export");
        Assert.DoesNotContain(operations, o => o.Path.StartsWith("/api/settings", StringComparison.Ordinal));
        Assert.DoesNotContain(operations, o => o.Path.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_operation_has_a_unique_id_and_a_summary()
    {
        var doc = await Get("/openapi/v1.json");

        var ids = new List<string>();
        foreach (var (_, path) in doc["paths"]!.AsObject())
        {
            foreach (var (_, operation) in path!.AsObject())
            {
                ids.Add(operation!["operationId"]!.GetValue<string>());
                Assert.False(string.IsNullOrWhiteSpace(operation["summary"]?.GetValue<string>()), $"{operation["operationId"]} has no summary");
            }
        }

        Assert.True(ids.Count >= 20, $"only {ids.Count} operations");
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task OpenApi_requires_the_bearer_token_for_calls()
    {
        var doc = await Get("/openapi/v1.json");

        Assert.Equal("http", doc["components"]!["securitySchemes"]!["bearer"]!["type"]!.GetValue<string>());
        Assert.Equal("bearer", doc["components"]!["securitySchemes"]!["bearer"]!["scheme"]!.GetValue<string>());
        Assert.NotNull(doc["security"]![0]!["bearer"]);
    }

    [Fact]
    public async Task Swagger_2_document_is_available_for_copilot_studio()
    {
        var doc = await Get("/openapi/swagger2.json");

        Assert.Equal("2.0", doc["swagger"]!.GetValue<string>());
        Assert.Contains(("post", "/api/items", "createTask"), Operations(doc));
        Assert.Contains(("get", "/api/dashboard", "getDashboard"), Operations(doc));
        // Swagger 2.0 has no bearer type: an API key in the Authorization header ("Bearer <token>").
        var scheme = doc["securityDefinitions"]!["bearer"]!;
        Assert.Equal("apiKey", scheme["type"]!.GetValue<string>());
        Assert.Equal("header", scheme["in"]!.GetValue<string>());
        Assert.Equal("Authorization", scheme["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_mapped_api_endpoint_is_either_described_or_deliberately_hidden()
    {
        // Adding an endpoint without deciding whether AI tools should see it fails here.
        var unclassified = OpenApiSetup.UnclassifiedRoutes(_server.App);

        Assert.Empty(unclassified);
    }

    private async Task<JsonNode> Get(string path)
    {
        using var client = _server.Client(authenticated: false);
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static List<(string Method, string Path, string Id)> Operations(JsonNode doc) =>
        doc["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject().Select(o => (o.Key, p.Key, o.Value!["operationId"]?.GetValue<string>() ?? string.Empty)))
            .ToList();
}
