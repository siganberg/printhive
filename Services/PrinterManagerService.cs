using BambuApi.Models;
using System.Collections.Concurrent;

namespace BambuApi.Services;

public class PrinterManagerService : IDisposable
{
    private readonly ConcurrentDictionary<string, BambuPrinterService> _printerServices = new();
    private readonly ConcurrentDictionary<string, PrinterConfig> _printerConfigs = new();
    private readonly ILogger<PrinterManagerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PrinterDataService _dataService;
    private bool _disposed = false;

    public PrinterManagerService(ILogger<PrinterManagerService> logger, IServiceProvider serviceProvider, PrinterDataService dataService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _dataService = dataService;
        LoadPrintersFromDatabase();
    }

    public async Task<string> AddPrinterAsync(AddPrinterRequest request, bool testConnection = false)
    {
        var config = new PrinterConfig
        {
            Name = request.Name,
            IpAddress = request.IpAddress,
            AccessCode = request.AccessCode,
            SerialNumber = request.SerialNumber
        };

        // Test connection if requested
        if (testConnection)
        {
            await TestPrinterConnectionAsync(config.IpAddress, config.AccessCode);
        }

        var savedId = _dataService.AddPrinter(config);
        config.Id = savedId;
        _printerConfigs[config.Id] = config;
        
        _logger.LogInformation("Added printer {Name} ({IpAddress})", config.Name, config.IpAddress);
        return config.Id;
    }

    public async Task<bool> TestPrinterConnectionAsync(string ipAddress, string accessCode)
    {
        var logger = _serviceProvider.GetRequiredService<ILogger<BambuPrinterService>>();
        var testService = new BambuPrinterService(ipAddress, accessCode, logger);
        
        try
        {
            var connected = await testService.ConnectAsync();
            if (!connected)
            {
                throw new InvalidOperationException("Failed to connect to printer");
            }
            return true;
        }
        finally
        {
            testService.Dispose();
        }
    }

    public Task<bool> UpdatePrinterAsync(string printerId, EditPrinterRequest request)
    {
        if (!_printerConfigs.TryGetValue(printerId, out var existingConfig))
        {
            return Task.FromResult(false);
        }

        // If IP or access code changed, dispose the old service to force reconnection
        if (existingConfig.IpAddress != request.IpAddress || existingConfig.AccessCode != request.AccessCode)
        {
            if (_printerServices.TryRemove(printerId, out var oldService))
            {
                oldService.Dispose();
                _logger.LogInformation("Disposed old service for printer {PrinterId} due to connection details change", printerId);
            }
        }

        // Update the configuration
        existingConfig.Name = request.Name;
        existingConfig.IpAddress = request.IpAddress;
        existingConfig.AccessCode = request.AccessCode;
        existingConfig.SerialNumber = request.SerialNumber;
        existingConfig.IsEnabled = request.IsEnabled;

        _dataService.UpdatePrinter(existingConfig);
        
        _logger.LogInformation("Updated printer {Name} ({PrinterId})", existingConfig.Name, printerId);
        return Task.FromResult(true);
    }

    public Task<bool> RemovePrinterAsync(string printerId)
    {
        if (_printerServices.TryRemove(printerId, out var service))
        {
            service.Dispose();
        }

        var removed = _printerConfigs.TryRemove(printerId, out var config);
        if (removed)
        {
            _dataService.RemovePrinter(printerId);
            _logger.LogInformation("Removed printer {Name}", config?.Name);
        }

        return Task.FromResult(removed);
    }

    public IEnumerable<PrinterConfig> GetAllPrinters()
    {
        return _printerConfigs.Values.ToList();
    }

    public PrinterConfig? GetPrinter(string printerId)
    {
        return _printerConfigs.TryGetValue(printerId, out var config) ? config : null;
    }

    public async Task<PrinterWithStatus> GetPrinterWithStatusAsync(string printerId)
    {
        var config = GetPrinter(printerId);
        if (config == null)
        {
            throw new ArgumentException($"Printer with ID {printerId} not found");
        }

        var result = new PrinterWithStatus { Config = config };

        // Check if printer is enabled before attempting connection
        if (!config.IsEnabled)
        {
            result.IsConnected = false;
            _logger.LogDebug("Skipping connection to disabled printer {PrinterId}", printerId);
            return result;
        }

        try
        {
            var service = GetOrCreatePrinterService(printerId);
            result.Status = await service.GetStatusAsync();
            result.IsConnected = result.Status.IsOnline;
            
            // Update config with detected model and firmware if available
            if (!string.IsNullOrEmpty(result.Status.Model) && result.Config.Model != result.Status.Model)
            {
                result.Config.Model = result.Status.Model;
                _dataService.UpdatePrinter(result.Config);
            }
            
            if (!string.IsNullOrEmpty(result.Status.FirmwareVersion) && result.Config.FirmwareVersion != result.Status.FirmwareVersion)
            {
                result.Config.FirmwareVersion = result.Status.FirmwareVersion;
                _dataService.UpdatePrinter(result.Config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get status for printer {PrinterId}", printerId);
            result.IsConnected = false;
            result.ConnectionError = ex.Message;
        }

        return result;
    }

    public async Task<List<PrinterWithStatus>> GetAllPrintersWithStatusAsync()
    {
        var tasks = _printerConfigs.Keys.Select(GetPrinterWithStatusAsync);
        return (await Task.WhenAll(tasks)).ToList();
    }

    public BambuPrinterService GetOrCreatePrinterService(string printerId)
    {
        return _printerServices.GetOrAdd(printerId, id =>
        {
            var config = _printerConfigs[id];
            var logger = _serviceProvider.GetRequiredService<ILogger<BambuPrinterService>>();
            return new BambuPrinterService(config.IpAddress, config.AccessCode, logger);
        });
    }

    private void LoadPrintersFromDatabase()
    {
        try
        {
            var configs = _dataService.GetAllPrinters();
            
            foreach (var config in configs)
            {
                _printerConfigs[config.Id] = config;
            }
            
            _logger.LogInformation("Loaded {Count} printers from database", configs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load printers from database");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var service in _printerServices.Values)
            {
                service.Dispose();
            }
            _printerServices.Clear();
            _dataService?.Dispose();
            _disposed = true;
        }
    }
}