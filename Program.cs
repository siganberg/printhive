using BambuApi.Services;
using BambuApi.Models;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", theme: AnsiConsoleTheme.Code)
    .CreateLogger();

try
{
    // Replace default logging with Serilog
    builder.Host.UseSerilog();

// Register services
builder.Services.AddSingleton<PrinterDataService>();
builder.Services.AddSingleton<PrinterManagerService>();
builder.Services.AddScoped<PrinterDiscoveryService>();
builder.Services.AddHttpClient();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting Bambu Lab Multi-Printer API");

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

app.MapPost("/api/printers/{printerId}/upload", async (string printerId, IFormFile file, PrinterManagerService printerManager) =>
{
    try
    {
        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "No file provided" });
        }

        if (!file.FileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Only 3MF files are supported" });
        }

        var printer = printerManager.GetPrinter(printerId);
        if (printer == null)
        {
            return Results.NotFound($"Printer with ID {printerId} not found");
        }

        if (!printer.IsEnabled)
        {
            return Results.BadRequest(new { error = "Printer is disabled" });
        }

        // Get or create printer service for this printer
        var printerService = printerManager.GetOrCreatePrinterService(printerId);
        
        // Upload file directly to the printer
        using var fileStream = file.OpenReadStream();
        var (success, message) = await printerService.UploadFileAsync(fileStream, file.FileName);

        if (success)
        {
            Log.Information("Successfully uploaded 3MF file {FileName} ({Size} bytes) to printer {PrinterId}", 
                file.FileName, file.Length, printerId);

            return Results.Ok(new { 
                message = "File uploaded successfully to printer", 
                fileName = file.FileName,
                size = file.Length,
                printer = printer.Name
            });
        }
        else
        {
            Log.Warning("Failed to upload 3MF file {FileName} to printer {PrinterId}: {Error}", 
                file.FileName, printerId, message);
                
            return Results.BadRequest(new { error = message });
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to upload file for printer {PrinterId}", printerId);
        return Results.Problem($"Upload failed: {ex.Message}");
    }
}).DisableAntiforgery();

app.MapGet("/api/discover", async (PrinterDiscoveryService discoveryService, PrinterManagerService printerManager) =>
{
    try
    {
        // Get existing printer IPs to filter them out
        var existingPrinters = printerManager.GetAllPrinters();
        var existingIps = existingPrinters.Select(p => p.IpAddress);
        
        var discoveredPrinters = await discoveryService.DiscoverPrintersAsync(existingIps);
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
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
