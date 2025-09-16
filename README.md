# Bambu Lab Multi-Printer API

A .NET web application to manage and monitor multiple Bambu Lab 3D printers simultaneously.

## Features

- **Multi-Printer Support**: Add, remove, and manage multiple Bambu Lab printers
- **Real-time Monitoring**: Display live status for all connected printers including:
  - Print progress and current tasks
  - Temperatures (bed and extruder)
  - Printer states (Idle, Printing, Paused, Finished, Failed)
  - Connection status
- **Web Dashboard**: Modern responsive interface with:
  - Grid view of all printers
  - Individual printer cards with status
  - Add/Remove printer functionality
  - Auto-refresh every 10 seconds
- **RESTful API**: Complete API for printer management
- **Persistent Configuration**: Printer configurations saved to JSON file

## Setup

### Prerequisites

- .NET 8.0 or later
- Bambu Lab printers with LAN mode enabled
- Access codes from your printers

### Getting Your Printer's IP and Access Code

1. On each Bambu Lab printer, enable LAN Mode in the network settings
2. Note down the IP address displayed
3. Find the access code in the printer's LAN mode settings (usually an 8-digit code)

### Running the Application

```bash
dotnet run
```

The application will start on `http://localhost:5000` (or `https://localhost:5001` for HTTPS).

## Usage

### Web Interface

1. Open your browser to `http://localhost:5000`
2. Click "Add Printer" to add your first printer
3. Fill in the printer name, IP address, and access code
4. The printer will be tested and added if connection is successful
5. View real-time status of all your printers on the dashboard

### API Endpoints

#### Printer Management
- `GET /api/printers` - Get all printers with their current status
- `GET /api/printers/{id}` - Get specific printer with status
- `POST /api/printers` - Add a new printer
- `PUT /api/printers/{id}` - Update an existing printer
- `DELETE /api/printers/{id}` - Remove a printer
- `POST /api/printers/test` - Test connection to a printer without adding it

#### Legacy Compatibility
- `GET /printer/status` - Returns status of first printer (backward compatibility)

### API Usage Examples

**Add a new printer:**
```bash
curl -X POST http://localhost:5000/api/printers \
  -H "Content-Type: application/json" \
  -d '{
    "name": "X1 Carbon",
    "ipAddress": "192.168.1.100",
    "accessCode": "12345678"
  }'
```

**Get all printers:**
```bash
curl http://localhost:5000/api/printers
```

**Update a printer:**
```bash
curl -X PUT http://localhost:5000/api/printers/{printer-id} \
  -H "Content-Type: application/json" \
  -d '{
    "name": "X1 Carbon Updated",
    "ipAddress": "192.168.1.101",
    "accessCode": "87654321",
    "serialNumber": "00M00A000000001",
    "isEnabled": true
  }'
```

**Test connection:**
```bash
curl -X POST http://localhost:5000/api/printers/test \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Test Printer",
    "ipAddress": "192.168.1.100",
    "accessCode": "12345678"
  }'
```

**Remove a printer:**
```bash
curl -X DELETE http://localhost:5000/api/printers/{printer-id}
```

## Configuration

Printer configurations are automatically saved to `printers.json` in the application directory. This file is created automatically when you add your first printer.

## Technical Details

- **Communication**: MQTT over TLS on port 8883
- **Authentication**: Uses "bblp" username and printer access code
- **Data Format**: JSON for all API responses
- **Security**: TLS certificate validation disabled for self-signed printer certificates
- **Persistence**: SQLite-free JSON file storage for printer configurations

## Troubleshooting

- **Connection Issues**: Ensure printers are in LAN mode and accessible on the network
- **Access Code**: Verify the 8-digit access code from the printer's LAN settings
- **Firewall**: Ensure port 8883 is not blocked for outbound connections
- **Network**: Check that the application and printers are on the same network segment

## Notes

- Printers must be in LAN mode for the application to connect
- The application maintains persistent MQTT connections to each printer
- Status updates are real-time when printers are actively sending data
- Connection testing is performed when adding new printers