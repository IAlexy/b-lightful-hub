namespace B2B.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

public class DevEnvironmentTests
{
    [Fact]
    public async Task DevelopmentEnvironment_Should_ExposeHealthWithSeededMockData_AndServeDevUiAndSwagger()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // 1. /health reports healthy Development environment with full mockup seed data
        var healthRes = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);

        using var healthDoc = await healthRes.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(healthDoc);
        var root = healthDoc.RootElement;
        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.Equal("Development", root.GetProperty("environment").GetString());
        Assert.True(root.GetProperty("mockDataSeeded").GetBoolean());

        var summary = root.GetProperty("summary");
        Assert.True(summary.GetProperty("products").GetInt32() >= 6);
        Assert.True(summary.GetProperty("customers").GetInt32() >= 3);
        Assert.True(summary.GetProperty("orders").GetInt32() >= 3);
        Assert.True(summary.GetProperty("invoices").GetInt32() >= 2);
        Assert.True(summary.GetProperty("shipments").GetInt32() >= 2);

        // 2. Root redirects to /dev-ui in Development
        var rootRes = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, rootRes.StatusCode);
        Assert.Equal("/dev-ui", rootRes.Headers.Location?.ToString());

        // 3. /dev-ui serves the built-in Development Portal HTML
        var uiRes = await client.GetAsync("/dev-ui");
        Assert.Equal(HttpStatusCode.OK, uiRes.StatusCode);
        var uiHtml = await uiRes.Content.ReadAsStringAsync();
        Assert.Contains("B-Lightful Hub — Dev Portal", uiHtml);

        // 4. Swagger OpenAPI JSON is available in Development
        var swaggerRes = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, swaggerRes.StatusCode);
    }
}
