namespace B2B.GraphQL.Gateway.DevEnvironment;

public static class DevEnvironmentEndpoints
{
    public static void MapHealthEndpoint(this WebApplication app, DevSeedSummary seedSummary)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            environment = app.Environment.EnvironmentName,
            mockDataSeeded = seedSummary.MockDataSeeded,
            summary = new
            {
                products = seedSummary.ProductsCount,
                customers = seedSummary.CustomersCount,
                orders = seedSummary.OrdersCount,
                invoices = seedSummary.InvoicesCount,
                shipments = seedSummary.ShipmentsCount
            },
            timestampUtc = DateTime.UtcNow
        }))
        .WithName("GetHealthStatus")
        .WithOpenApi();
    }

    public static void MapDevEnvironmentUi(this WebApplication app)
    {
        var htmlPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "dev-ui", "index.html");
        if (!File.Exists(htmlPath))
        {
            htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "dev-ui", "index.html");
        }

        app.MapGet("/", () => Results.Redirect("/dev-ui"))
           .ExcludeFromDescription();

        app.MapGet("/dev-ui", async () =>
        {
            if (!File.Exists(htmlPath))
            {
                return Results.NotFound(new { errorCode = "DEV_UI_NOT_FOUND", message = "Development UI file was not found." });
            }

            var html = await File.ReadAllTextAsync(htmlPath);
            return Results.Content(html, "text/html; charset=utf-8");
        })
        .ExcludeFromDescription();
    }
}
