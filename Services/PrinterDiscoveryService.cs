using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BambuApi.Models;

namespace BambuApi.Services;

public class PrinterDiscoveryService
{
    private readonly ILogger<PrinterDiscoveryService> _logger;
    private readonly HttpClient _httpClient;

    public PrinterDiscoveryService(ILogger<PrinterDiscoveryService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(3); // Short timeout for discovery
    }

    public async Task<List<DiscoveredPrinter>> DiscoverPrintersAsync()
    {
        var discoveredPrinters = new List<DiscoveredPrinter>();
        var tasks = new List<Task<List<DiscoveredPrinter>>>();

        _logger.LogInformation("Starting printer discovery...");

        // Method 1: SSDP Discovery (most reliable for Bambu printers)
        tasks.Add(DiscoverViaSsdpAsync());

        // Method 2: Local network scan
        tasks.Add(DiscoverViaNetworkScanAsync());

        try
        {
            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                discoveredPrinters.AddRange(result);
            }

            // Remove duplicates based on IP address
            var uniquePrinters = discoveredPrinters
                .GroupBy(p => p.IpAddress)
                .Select(g => g.First())
                .ToList();

            _logger.LogInformation("Discovery completed. Found {Count} unique printers", uniquePrinters.Count);
            return uniquePrinters;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during printer discovery");
            return discoveredPrinters;
        }
    }

    public async Task<List<DiscoveredPrinter>> DiscoverPrintersAsync(IEnumerable<string> existingPrinterIps)
    {
        var discoveredPrinters = await DiscoverPrintersAsync();
        var existingIps = existingPrinterIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Filter out printers that already exist
        var newPrinters = discoveredPrinters
            .Where(p => !existingIps.Contains(p.IpAddress))
            .ToList();

        _logger.LogInformation("Found {NewCount} new printers (filtered out {ExistingCount} existing)", 
            newPrinters.Count, discoveredPrinters.Count - newPrinters.Count);
        
        return newPrinters;
    }

    private async Task<List<DiscoveredPrinter>> DiscoverViaSsdpAsync()
    {
        var discoveredPrinters = new List<DiscoveredPrinter>();

        try
        {
            _logger.LogDebug("Starting SSDP discovery...");

            // Listen for multicast SSDP messages
            await ListenForSsdpMulticast(discoveredPrinters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSDP discovery failed");
        }

        return discoveredPrinters;
    }

    private async Task ListenForSsdpMulticast(List<DiscoveredPrinter> discoveredPrinters)
    {
        try
        {
            var multicastAddress = IPAddress.Parse("239.255.255.250");
            var localEndPoint = new IPEndPoint(IPAddress.Any, 0); // Use any available port
            
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(localEndPoint);
            
            // Join multicast group
            udpClient.JoinMulticastGroup(multicastAddress);
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 1000);

            _logger.LogDebug("Listening for SSDP multicast messages...");
            
            var timeout = DateTime.UtcNow.AddSeconds(5);
            
            while (DateTime.UtcNow < timeout)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    var result = await udpClient.ReceiveAsync().WaitAsync(cts.Token);
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    
                    _logger.LogDebug("Received SSDP message from {IP}: {Message}", 
                        result.RemoteEndPoint.Address, message.Substring(0, Math.Min(100, message.Length)));
                    
                    if (message.Contains("bambulab", StringComparison.OrdinalIgnoreCase) || 
                        message.Contains("3dprinter", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("urn:bambulab-com:device", StringComparison.OrdinalIgnoreCase))
                    {
                        var printer = ParseSsdpMessage(message, result.RemoteEndPoint.Address.ToString());
                        if (printer != null)
                        {
                            discoveredPrinters.Add(printer);
                            _logger.LogDebug("Found printer via SSDP: {IP} - {Name}", printer.IpAddress, printer.DeviceName);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout is expected, continue listening
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    // Timeout is expected, continue listening
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    // Client was disposed, exit
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug("Socket error during SSDP listen: {Error}", ex.Message);
                    break;
                }
            }
            
            udpClient.DropMulticastGroup(multicastAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error listening for SSDP multicast");
        }
    }


    private DiscoveredPrinter? ParseSsdpMessage(string message, string ipAddress)
    {
        try
        {
            var printer = new DiscoveredPrinter
            {
                IpAddress = ipAddress,
                DiscoveryMethod = "SSDP"
            };

            // Parse SSDP headers
            var lines = message.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("SERVER:", StringComparison.OrdinalIgnoreCase))
                {
                    printer.FirmwareVersion = ExtractValueFromHeader(trimmedLine);
                }
                else if (trimmedLine.StartsWith("USN:", StringComparison.OrdinalIgnoreCase))
                {
                    var usn = ExtractValueFromHeader(trimmedLine);
                    if (usn.Contains("::"))
                    {
                        printer.SerialNumber = usn.Split("::").LastOrDefault() ?? "";
                    }
                }
                else if (trimmedLine.StartsWith("NT:", StringComparison.OrdinalIgnoreCase))
                {
                    printer.DeviceType = ExtractValueFromHeader(trimmedLine);
                }
            }

            // Try to determine model from IP or other info
            printer.Model = DetermineModelFromSsdp(message);
            printer.DeviceName = string.IsNullOrEmpty(printer.Model) ? "Bambu Lab Printer" : printer.Model;

            return printer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SSDP message");
            return null;
        }
    }

    private async Task<List<DiscoveredPrinter>> DiscoverViaNetworkScanAsync()
    {
        var discoveredPrinters = new List<DiscoveredPrinter>();

        try
        {
            _logger.LogDebug("Starting network scan discovery...");

            // Get local network ranges
            var networkRanges = GetLocalNetworkRanges();
            var scanTasks = new List<Task>();

            foreach (var range in networkRanges)
            {
                scanTasks.Add(ScanNetworkRange(range, discoveredPrinters));
            }

            await Task.WhenAll(scanTasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Network scan discovery failed");
        }

        return discoveredPrinters;
    }

    private async Task ScanNetworkRange(string networkRange, List<DiscoveredPrinter> discoveredPrinters)
    {
        var tasks = new List<Task>();
        var parts = networkRange.Split('.');
        var baseIp = $"{parts[0]}.{parts[1]}.{parts[2]}";

        // Scan common IP ranges (limit to avoid overwhelming the network)
        for (int i = 1; i <= 254; i += 5) // Sample every 5th IP for faster scanning
        {
            var endRange = Math.Min(i + 4, 254);
            for (int j = i; j <= endRange; j++)
            {
                var ip = $"{baseIp}.{j}";
                tasks.Add(CheckForBambuPrinter(ip, discoveredPrinters));
            }

            // Limit concurrent scans
            if (tasks.Count >= 20)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        if (tasks.Any())
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task CheckForBambuPrinter(string ip, List<DiscoveredPrinter> discoveredPrinters)
    {
        try
        {
            // Try to connect to known Bambu MQTT port
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(ip, 8883);
            
            if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask && tcpClient.Connected)
            {
                var printer = new DiscoveredPrinter
                {
                    IpAddress = ip,
                    DeviceName = "Bambu Lab Printer",
                    Model = "Unknown Model",
                    DiscoveryMethod = "Port Scan",
                    DiscoveredAt = DateTime.UtcNow
                };

                discoveredPrinters.Add(printer);
                _logger.LogDebug("Found potential Bambu printer via port scan: {IP}", ip);
            }
        }
        catch
        {
            // Not a Bambu printer or not reachable
        }
    }

    private List<string> GetLocalNetworkRanges()
    {
        var ranges = new List<string>();

        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback && 
                            ni.OperationalStatus == OperationalStatus.Up);

            foreach (var ni in networkInterfaces)
            {
                var ipProperties = ni.GetIPProperties();
                foreach (var addr in ipProperties.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || 
                            (ip.StartsWith("172.") && IsPrivateClassB(ip)))
                        {
                            ranges.Add(ip);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get local network ranges");
        }

        return ranges;
    }

    private static bool IsPrivateClassB(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var secondOctet))
        {
            return secondOctet >= 16 && secondOctet <= 31;
        }
        return false;
    }

    private static string ExtractValueFromHeader(string headerLine)
    {
        var colonIndex = headerLine.IndexOf(':');
        return colonIndex >= 0 ? headerLine.Substring(colonIndex + 1).Trim() : "";
    }

    private static string DetermineModelFromSsdp(string message)
    {
        if (message.Contains("X1C") || message.Contains("x1c"))
            return "X1 Carbon";
        if (message.Contains("X1") || message.Contains("x1"))
            return "X1";
        if (message.Contains("P1S") || message.Contains("p1s"))
            return "P1S";
        if (message.Contains("P1P") || message.Contains("p1p"))
            return "P1P";
        if (message.Contains("A1") || message.Contains("a1"))
            return "A1";
        
        return "Bambu Lab Printer";
    }
}