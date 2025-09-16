using BambuApi.Models;
using LiteDB;

namespace BambuApi.Services;

public class PrinterDataService : IDisposable
{
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<PrinterConfig> _printers;

    public PrinterDataService(string connectionString = "printers.db")
    {
        _database = new LiteDatabase(connectionString);
        _printers = _database.GetCollection<PrinterConfig>("printers");
        
        _printers.EnsureIndex(x => x.Id);
        _printers.EnsureIndex(x => x.IpAddress);
    }

    public List<PrinterConfig> GetAllPrinters()
    {
        return _printers.FindAll().ToList();
    }

    public PrinterConfig? GetPrinter(string id)
    {
        return _printers.FindById(id);
    }

    public PrinterConfig? GetPrinterByIpAddress(string ipAddress)
    {
        return _printers.FindOne(x => x.IpAddress == ipAddress);
    }

    public string AddPrinter(PrinterConfig printer)
    {
        if (string.IsNullOrEmpty(printer.Id))
        {
            printer.Id = Guid.NewGuid().ToString();
        }
        
        _printers.Insert(printer);
        return printer.Id;
    }

    public bool UpdatePrinter(PrinterConfig printer)
    {
        return _printers.Update(printer);
    }

    public bool RemovePrinter(string id)
    {
        return _printers.Delete(id);
    }

    public bool PrinterExists(string ipAddress)
    {
        return _printers.Exists(x => x.IpAddress == ipAddress);
    }

    public void Dispose()
    {
        _database?.Dispose();
    }
}