namespace BambuApi.Models;

public class PrinterStatus
{
    public string State { get; set; } = string.Empty;
    public string SubState { get; set; } = string.Empty;
    public int Progress { get; set; }
    public string CurrentTask { get; set; } = string.Empty;
    public double BedTemperature { get; set; }
    public double ExtruderTemperature { get; set; }
    public string Filename { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
}