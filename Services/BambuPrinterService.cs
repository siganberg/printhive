using MQTTnet;
using System.Text.Json;
using System.Buffers;
using BambuApi.Models;

namespace BambuApi.Services;

public class BambuPrinterService : IDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly string _printerIp;
    private readonly string _accessCode;
    private PrinterStatus _lastStatus = new();
    private bool _disposed;
    private ILogger _logger;
    private bool _isConnected = false;

    public BambuPrinterService(string printerIp, string accessCode, ILogger logger)
    {
        _printerIp = printerIp;
        _accessCode = accessCode;
        _logger = logger;

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();
        
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
    }

    public async Task<bool> ConnectAsync()
    {
        if (_isConnected && _mqttClient.IsConnected)
        {
            return true;
        }

        try
        {
            if (!_mqttClient.IsConnected)
            {
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_printerIp, 8883)
                    .WithCredentials("bblp", _accessCode)
                    .WithTlsOptions(o => 
                    {
                        o.WithIgnoreCertificateChainErrors();
                        o.WithIgnoreCertificateRevocationErrors();
                        o.WithAllowUntrustedCertificates();
                        o.WithCertificateValidationHandler(_ => true);
                    })
                    .Build();

                await _mqttClient.ConnectAsync(options);
                
                await _mqttClient.SubscribeAsync($"device/{GetSerialNumber()}/report");
                
                _isConnected = true;
                _logger.LogInformation("Connected to Bambu printer at {PrinterIp}", _printerIp);
            }
            
            return _mqttClient.IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to printer: {Message}", ex.Message);
            _isConnected = false;
            return false;
        }
    }

    public async Task<PrinterStatus> GetStatusAsync()
    {
        if (!await ConnectAsync())
        {
            _lastStatus.IsOnline = false;
            return _lastStatus;
        }

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"device/{GetSerialNumber()}/request")
                .WithPayload(JsonSerializer.Serialize(new { pushing = new { sequence_id = "0", command = "pushall" } }))
                .Build();

            await _mqttClient.PublishAsync(message);
            
            _lastStatus.IsOnline = true;
            _logger.LogDebug("Status request sent to printer");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request status from printer");
            _lastStatus.IsOnline = false;
        }
        
        return _lastStatus;
    }

    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.Payload.IsSingleSegment ? 
                e.ApplicationMessage.Payload.FirstSpan : 
                e.ApplicationMessage.Payload.ToArray());
            
            _logger.LogDebug("Received message from topic {Topic}: {Payload}", e.ApplicationMessage.Topic, payload);
            
            var data = JsonSerializer.Deserialize<JsonElement>(payload);

            if (data.TryGetProperty("print", out var printData))
            {
                // Parse mc_print_stage (could be string or number)
                if (printData.TryGetProperty("mc_print_stage", out var stage))
                {
                    int stageNum = 0;
                    if (stage.ValueKind == JsonValueKind.String)
                    {
                        int.TryParse(stage.GetString(), out stageNum);
                    }
                    else if (stage.ValueKind == JsonValueKind.Number)
                    {
                        stageNum = stage.GetInt32();
                    }
                    
                    _lastStatus.State = stageNum switch
                    {
                        0 => "Idle",
                        1 => "Printing",
                        2 => "Paused",
                        3 => "Finished",
                        4 => "Failed",
                        _ => $"Stage {stageNum}"
                    };
                }
                
                // Parse mc_print_sub_stage (could be string or number)
                if (printData.TryGetProperty("mc_print_sub_stage", out var subStage))
                {
                    if (subStage.ValueKind == JsonValueKind.String)
                        _lastStatus.SubState = subStage.GetString() ?? "";
                    else if (subStage.ValueKind == JsonValueKind.Number)
                        _lastStatus.SubState = subStage.GetInt32().ToString();
                }
                
                // Parse mc_percent (could be string or number)
                if (printData.TryGetProperty("mc_percent", out var progress))
                {
                    if (progress.ValueKind == JsonValueKind.String)
                    {
                        if (int.TryParse(progress.GetString(), out var progressInt))
                            _lastStatus.Progress = progressInt;
                    }
                    else if (progress.ValueKind == JsonValueKind.Number)
                    {
                        _lastStatus.Progress = progress.GetInt32();
                    }
                }
                
                // Parse subtask_name (string)
                if (printData.TryGetProperty("subtask_name", out var task))
                    _lastStatus.CurrentTask = task.GetString() ?? "";
                
                // Parse bed_temper (could be string or number)
                if (printData.TryGetProperty("bed_temper", out var bedTemp))
                {
                    if (bedTemp.ValueKind == JsonValueKind.String)
                    {
                        if (double.TryParse(bedTemp.GetString(), out var tempDouble))
                            _lastStatus.BedTemperature = tempDouble;
                    }
                    else if (bedTemp.ValueKind == JsonValueKind.Number)
                    {
                        _lastStatus.BedTemperature = bedTemp.GetDouble();
                    }
                }
                
                // Parse nozzle_temper (could be string or number)
                if (printData.TryGetProperty("nozzle_temper", out var extruderTemp))
                {
                    if (extruderTemp.ValueKind == JsonValueKind.String)
                    {
                        if (double.TryParse(extruderTemp.GetString(), out var tempDouble))
                            _lastStatus.ExtruderTemperature = tempDouble;
                    }
                    else if (extruderTemp.ValueKind == JsonValueKind.Number)
                    {
                        _lastStatus.ExtruderTemperature = extruderTemp.GetDouble();
                    }
                }
                
                // Parse gcode_file (string)
                if (printData.TryGetProperty("gcode_file", out var filename))
                    _lastStatus.Filename = filename.GetString() ?? "";

                // Parse firmware version
                if (printData.TryGetProperty("ver", out var version))
                    _lastStatus.FirmwareVersion = version.GetString() ?? "";

                // Try to determine printer model from various sources
                DetectPrinterModel(printData);
                
                _logger.LogDebug("Updated printer status: State={State}, Progress={Progress}%, Model={Model}", 
                    _lastStatus.State, _lastStatus.Progress, _lastStatus.Model);
            }

            _lastStatus.LastUpdated = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse message from printer");
        }

        return Task.CompletedTask;
    }

    private void DetectPrinterModel(JsonElement printData)
    {
        // Try multiple approaches to detect the printer model
        
        // Method 1: Check device type field
        if (printData.TryGetProperty("device", out var deviceData))
        {
            if (deviceData.TryGetProperty("type", out var deviceType))
            {
                var typeNum = deviceType.GetInt32();
                _lastStatus.Model = typeNum switch
                {
                    1 => "X1 Carbon",
                    2 => "X1",
                    3 => "P1P",
                    4 => "P1S",
                    5 => "A1 mini",
                    6 => "A1",
                    _ => $"Unknown Model (Type {typeNum})"
                };
            }
        }

        // Method 2: Check upgrade state serial number pattern
        if (string.IsNullOrEmpty(_lastStatus.Model) && printData.TryGetProperty("upgrade_state", out var upgradeState))
        {
            if (upgradeState.TryGetProperty("sn", out var serialNum))
            {
                var sn = serialNum.GetString() ?? "";
                _lastStatus.Model = DetectModelFromSerial(sn);
            }
        }

        // Method 3: Fallback to serial number pattern analysis
        if (string.IsNullOrEmpty(_lastStatus.Model))
        {
            _lastStatus.Model = DetectModelFromSerial(GetSerialNumber());
        }

        // If still no model detected, use a generic name
        if (string.IsNullOrEmpty(_lastStatus.Model))
        {
            _lastStatus.Model = "Bambu Lab Printer";
        }
    }

    private string DetectModelFromSerial(string serialNumber)
    {
        if (string.IsNullOrEmpty(serialNumber) || serialNumber.Length < 3)
            return "";

        // Bambu Lab serial number patterns (first 3 characters typically indicate model)
        var prefix = serialNumber.Substring(0, Math.Min(3, serialNumber.Length));
        
        return prefix switch
        {
            "01S" => "X1 Carbon",
            "01P" => "X1",
            "02S" => "P1S", 
            "02P" => "P1P",
            "03S" => "A1",
            "03A" => "A1 mini",
            "00M" => "X1 Carbon", // Your printer's pattern
            _ => $"Bambu Lab Printer ({prefix})"
        };
    }

    private string GetSerialNumber()
    {
        return "00M00A252000770";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient.Dispose();
            _disposed = true;
        }
    }
}