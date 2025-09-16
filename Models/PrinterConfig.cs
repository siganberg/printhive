using LiteDB;

namespace BambuApi.Models;

public class PrinterConfig
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string AccessCode { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConnected { get; set; }
    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
}

public class PrinterWithStatus
{
    public PrinterConfig Config { get; set; } = new();
    public PrinterStatus Status { get; set; } = new();
    public bool IsConnected { get; set; }
    public string ConnectionError { get; set; } = string.Empty;
}