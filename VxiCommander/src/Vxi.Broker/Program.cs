using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vxi.Protocol;
using Vxi.Core;
using Vxi.Transport.Gpib;

string configPath = Environment.GetEnvironmentVariable("VXI_CONFIG")
    ?? "/etc/vxi-controller/appsettings.json";

var cfg = ConfigLoader.Load(configPath);
var runner = new DriverRunner(cfg);
var audit = new AuditLog(cfg.AuditLogPath);
var inventory = new InventoryStore(cfg.InventoryPath);
var gpib = new Dictionary<string, LinuxGpib>();
var gpibGate = new SemaphoreSlim(1, 1);

if (File.Exists(cfg.SocketPath))
    File.Delete(cfg.SocketPath);

Directory.CreateDirectory(Path.GetDirectoryName(cfg.SocketPath)!);

var endpoint = new UnixDomainSocketEndPoint(cfg.SocketPath);
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(endpoint);
listener.Listen(20);

if (OperatingSystem.IsLinux())
{
    File.SetUnixFileMode(
        cfg.SocketPath,
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite);
}

Console.WriteLine($"VXI broker listening on {cfg.SocketPath}");

if (cfg.Web?.Enabled == true)
    _ = Task.Run(() => RunWebAsync(cfg.Web));

while (true)
{
    var client = await listener.AcceptAsync();
    _ = Task.Run(() => HandleSocket(client));
}

async Task HandleSocket(Socket socket)
{
    await using var stream = new NetworkStream(socket, ownsSocket: true);
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
    {
        AutoFlush = true
    };

    try
    {
        string? line = await reader.ReadLineAsync();
        var request = JsonSerializer.Deserialize<BrokerRequest>(line!, JsonDefaults.Options)
            ?? throw new InvalidDataException("Invalid request");

        object data = await Dispatch(request);
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(new BrokerResponse(true, data), JsonDefaults.Options));
    }
    catch (Exception ex)
    {
        await audit.WriteAsync(new { type = "error", message = ex.Message });
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(new BrokerResponse(false, Error: ex.Message), JsonDefaults.Options));
    }
}

async Task<object> Dispatch(BrokerRequest request) => request.Action switch
{
    "status" => new
    {
        status = "ready",
        drivers = cfg.Drivers.Length,
        instruments = inventory.Snapshot().Count,
        web = cfg.Web?.Enabled == true
            ? $"http://{cfg.Web.BindAddress}:{cfg.Web.Port}/"
            : null
    },
    "drivers" => cfg.Drivers.Select(x => new { x.Id, x.Executable }),
    "devices" => inventory.Snapshot(),
    "discover" => await Discover(),
    "describe" => await Describe(request),
    "operate" => await Operate(request),
    _ => throw new ArgumentException("Unknown action")
};

async Task<object> Discover()
{
    var results = new List<DiscoveredEndpoint>();

    foreach (var bus in cfg.Buses.Where(x => x.AutoDiscover))
    {
        if (!bus.Transport.Equals("linux-gpib", StringComparison.OrdinalIgnoreCase))
            continue;

        var scan = await GpibDiscovery.ScanAsync(
            bus.BoardIndex,
            bus.TimeoutCode,
            CancellationToken.None);

        results.AddRange(scan.Select(x => new DiscoveredEndpoint(
            bus.Id,
            x.BoardIndex,
            x.PrimaryAddress,
            x.SecondaryAddress,
            x.Identification,
            x.Kind,
            x.RawConfiguration,
            DateTimeOffset.UtcNow)));
    }

    await audit.WriteAsync(new { type = "discovery", count = results.Count });
    return results;
}

async Task<object> Describe(BrokerRequest request)
{
    var instrument = FindInstrument(request);
    var driver = runner.Find(instrument.DriverId);

    var response = await runner.InvokeAsync(
        driver,
        new(
            ProtocolConstants.Version,
            Guid.NewGuid().ToString("N"),
            "describe",
            instrument),
        CancellationToken.None);

    if (!response.Success)
        throw new InvalidOperationException(response.Error);

    return response.Operations!;
}

async Task<object> Operate(BrokerRequest request)
{
    var instrument = FindInstrument(request);

    if (string.IsNullOrWhiteSpace(request.OperationId))
        throw new ArgumentException("operationId required");

    var driver = runner.Find(instrument.DriverId);
    var response = await runner.InvokeAsync(
        driver,
        new(
            ProtocolConstants.Version,
            Guid.NewGuid().ToString("N"),
            "generate",
            instrument,
            request.OperationId,
            request.Parameters),
        CancellationToken.None);

    if (!response.Success)
        throw new InvalidOperationException(response.Error);

    var commands = response.Commands ?? [];

    if (commands.Count > cfg.Security.MaxCommandsPerOperation)
        throw new InvalidOperationException("Too many commands");

    foreach (var command in commands)
    {
        if (command.Text.Contains('\n') ||
            command.Text.Contains('\r') ||
            command.Text.Contains(';'))
        {
            throw new InvalidOperationException("Unsafe command formatting");
        }

        if (!driver.AllowedCategories.Contains(command.Category, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Command category denied");

        if (!driver.AllowedCommandPrefixes.Any(prefix =>
                command.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Command prefix denied");
        }
    }

    var results = new List<object>();

    foreach (var command in commands)
    {
        string? instrumentResponse = null;

        if (!request.DryRun)
        {
            var device = await GetGpib(instrument.Address);
            instrumentResponse = await device.ExecuteAsync(
                command.Text,
                command.ExpectsResponse,
                CancellationToken.None);

            int delay = Math.Max(
                command.DelayAfterMilliseconds,
                command.Category == "relay-switch"
                    ? cfg.Security.MinimumRelayDelayMilliseconds
                    : 0);

            if (delay > 0)
                await Task.Delay(delay);
        }

        results.Add(new
        {
            command.Text,
            command.ExpectsResponse,
            command.Category,
            response = instrumentResponse
        });
    }

    await audit.WriteAsync(new
    {
        type = "operation",
        instrument = instrument.Id,
        operation = request.OperationId,
        dryRun = request.DryRun,
        commands = commands.Select(x => x.Text)
    });

    return results;
}

InstrumentInstance FindInstrument(BrokerRequest request) =>
    inventory.Find(request.InstrumentId ?? "");

async Task<LinuxGpib> GetGpib(InstrumentAddress address)
{
    string key = $"{address.BusId}:{address.PrimaryAddress}:{address.SecondaryAddress}";

    await gpibGate.WaitAsync();
    try
    {
        if (gpib.TryGetValue(key, out var existing))
            return existing;

        var bus = cfg.Buses.Single(x => x.Id == address.BusId);
        var created = new LinuxGpib(new(
            bus.BoardIndex,
            address.PrimaryAddress,
            address.SecondaryAddress,
            bus.TimeoutCode));

        gpib[key] = created;
        return created;
    }
    finally
    {
        gpibGate.Release();
    }
}

async Task RunWebAsync(WebConfig web)
{
    var http = new HttpListener();
    http.Prefixes.Add($"http://{web.BindAddress}:{web.Port}/");
    http.Start();

    Console.WriteLine(
        $"VXI web UI listening on http://{web.BindAddress}:{web.Port}/");

    while (true)
    {
        var context = await http.GetContextAsync();
        _ = Task.Run(() => HandleHttp(context));
    }
}

async Task HandleHttp(HttpListenerContext context)
{
    try
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";

        if (context.Request.HttpMethod == "GET" && path == "/")
        {
            await WebAssets.WriteAsync(context, "index.html");
            return;
        }

        if (context.Request.HttpMethod == "GET" && path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            await WebAssets.WriteAsync(context, path["/assets/".Length..]);
            return;
        }

        if (context.Request.HttpMethod == "GET" && path == "/api/status")
        {
            await Json(context, new
            {
                status = "ready",
                drivers = cfg.Drivers,
                instruments = inventory.Snapshot(),
                buses = cfg.Buses
            });
            return;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/discover")
        {
            await Json(context, await Discover());
            return;
        }

        if (context.Request.HttpMethod == "GET" && path == "/api/inventory")
        {
            await Json(context, inventory.Snapshot());
            return;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/inventory")
        {
            var items = await ReadJson<List<InstrumentInstance>>(context)
                ?? throw new InvalidDataException("Invalid inventory JSON");

            foreach (var item in items)
                runner.Find(item.DriverId);

            await inventory.ReplaceAsync(items);
            await audit.WriteAsync(new
            {
                type = "inventory-updated",
                count = items.Count,
                remote = context.Request.RemoteEndPoint?.ToString()
            });

            await Json(context, new { saved = true, count = items.Count });
            return;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/describe")
        {
            var request = await ReadJson<BrokerRequest>(context)
                ?? throw new InvalidDataException("Invalid describe JSON");

            await Json(context, await Dispatch(request));
            return;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/command")
        {
            var request = await ReadJson<BrokerRequest>(context)
                ?? throw new InvalidDataException("Invalid command JSON");

            await Json(context, await Dispatch(request));
            return;
        }

        context.Response.StatusCode = 404;
        await Json(context, new { error = "Not found" });
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 400;
        await Json(context, new { error = ex.Message });
    }
}

static async Task<T?> ReadJson<T>(HttpListenerContext context)
{
    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
    string body = await reader.ReadToEndAsync();
    return JsonSerializer.Deserialize<T>(body, JsonDefaults.Options);
}

static async Task Json(HttpListenerContext context, object value)
{
    string json = JsonSerializer.Serialize(
        value,
        new JsonSerializerOptions(JsonDefaults.Options)
        {
            WriteIndented = true
        });

    await WebAssets.WriteTextAsync(
        context,
        "application/json; charset=utf-8",
        json);
}
