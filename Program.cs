using BambuApi.Services;
using BambuApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<PrinterManagerService>();
builder.Services.AddScoped<PrinterDiscoveryService>();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api", () => "Bambu Lab Multi-Printer API");

// Printer management endpoints
app.MapGet("/api/printers", async (PrinterManagerService printerManager) =>
{
    var printers = await printerManager.GetAllPrintersWithStatusAsync();
    return Results.Ok(printers);
});

app.MapGet("/api/printers/{printerId}", async (string printerId, PrinterManagerService printerManager) =>
{
    try
    {
        var printer = await printerManager.GetPrinterWithStatusAsync(printerId);
        return Results.Ok(printer);
    }
    catch (ArgumentException)
    {
        return Results.NotFound($"Printer with ID {printerId} not found");
    }
});

app.MapPost("/api/printers", async (AddPrinterRequest request, PrinterManagerService printerManager) =>
{
    try
    {
        var printerId = await printerManager.AddPrinterAsync(request, testConnection: false);
        return Results.Created($"/api/printers/{printerId}", new { id = printerId });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/printers/test", async (AddPrinterRequest request, PrinterManagerService printerManager) =>
{
    try
    {
        var success = await printerManager.TestPrinterConnectionAsync(request.IpAddress, request.AccessCode);
        return Results.Ok(new { success, message = "Connection test successful" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

app.MapPut("/api/printers/{printerId}", async (string printerId, EditPrinterRequest request, PrinterManagerService printerManager) =>
{
    try
    {
        var updated = await printerManager.UpdatePrinterAsync(printerId, request);
        return updated ? Results.Ok(new { message = "Printer updated successfully" }) : Results.NotFound();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/printers/{printerId}", async (string printerId, PrinterManagerService printerManager) =>
{
    var removed = await printerManager.RemovePrinterAsync(printerId);
    return removed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/discover", async (PrinterDiscoveryService discoveryService) =>
{
    try
    {
        var discoveredPrinters = await discoveryService.DiscoverPrintersAsync();
        return Results.Ok(discoveredPrinters);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Discovery failed: {ex.Message}");
    }
});

// Legacy endpoint for backward compatibility
app.MapGet("/printer/status", async (PrinterManagerService printerManager) =>
{
    var printers = await printerManager.GetAllPrintersWithStatusAsync();
    return Results.Ok(printers.FirstOrDefault()?.Status ?? new PrinterStatus());
});

app.Run();
