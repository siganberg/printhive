using System.ComponentModel.DataAnnotations;

namespace BambuApi.Models;

public class EditPrinterRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    
    [Required]
    public string IpAddress { get; set; } = string.Empty;
    
    [Required]
    public string AccessCode { get; set; } = string.Empty;
    
    public string SerialNumber { get; set; } = string.Empty;
    
    public bool IsEnabled { get; set; } = true;
}