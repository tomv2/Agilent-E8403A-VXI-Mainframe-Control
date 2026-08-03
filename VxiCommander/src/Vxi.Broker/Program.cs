using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vxi.Core;
using Vxi.Protocol;
using Vxi.Transport.Gpib;

const string LiveConfirmationPhrase = "SWITCH RELAY";

string configPath = Environment.GetEnvironmentVariable("VXI_CONFIG") ?? "/etc/vxi-controller/appsettings.json";
AppConfig cfg = ConfigLoader.Load(configPath);
var runner = new DriverRunner(cfg);
var audit = new AuditLog(cfg.AuditLogPath);
var inventory = new InventoryStore(cfg.InventoryPath);
var gpib = new Dictionary<string, LinuxGpib>();
var gpibGate = new SemaphoreSlim(1, 1);

if (File.Exists(cfg.SocketPath)) File.Delete(cfg.SocketPath);
Directory.CreateDirectory(Path.GetDirectoryName(cfg.SocketPath)!);
var endpoint = new UnixDomainSocketEndPoint(cfg.SocketPath);
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(endpoint);
listener.Listen(20);
if (OperatingSystem.IsLinux())
{
    File.SetUnixFileMode(
        cfg.SocketPath,
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
}
Console.WriteLine($"VXI broker listening on {cfg.SocketPath}");

if (cfg.Web?.Enabled == true) _ = Task.Run(() => RunWebAsync(cfg.Web));

while (true)
{
    Socket client = await listener.AcceptAsync();
    _ = Task.Run(() => HandleSocket(client));
}

async Task HandleSocket(Socket socket)
{
    await using var stream = new NetworkStream(socket, ownsSocket: true);
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    try
    {
        string? line = await reader.ReadLineAsync();
        BrokerRequest request = JsonSerializer.Deserialize<BrokerRequest>(line!, JsonDefaults.Options)
            ?? throw new InvalidDataException("Invalid request");
        object data = await Dispatch(request);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new BrokerResponse(true, data), JsonDefaults.Options));
    }
    catch (Exception ex)
    {
        await audit.WriteAsync(new { type = "error", message = ex.Message });
        await writer.WriteLineAsync(JsonSerializer.Serialize(new BrokerResponse(false, Error: ex.Message), JsonDefaults.Options));
    }
}

async Task<object> Dispatch(BrokerRequest request) => request.Action switch
{
    "status" => new
    {
        status = "ready",
        drivers = cfg.Drivers.Length,
        instruments = inventory.Snapshot().Count,
        web = cfg.Web?.Enabled == true ? $"http://{cfg.Web.BindAddress}:{cfg.Web.Port}/" : null,
    },
    "drivers" => cfg.Drivers.Select(x => new { x.Id, x.Executable }),
    "devices" => inventory.Snapshot(),
    "discover" => await Discover(),
    "describe" => await Describe(request),
    "operate" => await Operate(request),
    _ => throw new ArgumentException("Unknown action"),
};

async Task<IReadOnlyList<MainframeDiscoveryView>> Discover()
{
    var results = new List<MainframeDiscoveryView>();
    foreach (BusConfig bus in cfg.Buses.Where(x => x.AutoDiscover && x.Transport.Equals("linux-gpib", StringComparison.OrdinalIgnoreCase)))
    {
        IReadOnlyList<GpibMainframeResult> scan = await GpibDiscovery.ScanAsync(bus.BoardIndex, bus.TimeoutCode, CancellationToken.None);
        results.AddRange(scan.Select(mainframe => new MainframeDiscoveryView(
            bus.Id,
            mainframe.BoardIndex,
            mainframe.PrimaryAddress,
            mainframe.Identification,
            mainframe.SwitchSecondaryAddress,
            mainframe.SwitchIdentification,
            mainframe.SwitchError,
            mainframe.Devices.Select((device, index) => new DiscoveredModuleView(
                device.LogicalAddress,
                device.PhysicalSlot,
                device.DeviceType,
                device.ManufacturerId,
                device.DeviceClass,
                device.AddressSpace,
                device.Status,
                device.Description,
                SuggestedCardNumber(device, index))).ToArray(),
            mainframe.RawConfiguration)));
    }

    await audit.WriteAsync(new { type = "discovery", mainframes = results.Count });
    return results;
}

static int? SuggestedCardNumber(VxiDeviceRecord device, int index)
{
    if (device.LogicalAddress == 0) return null;
    // The first register device in a Switchbox is card 1, with subsequent
    // logical addresses assigned in ascending order. Keep it a suggestion;
    // the web UI requires the user to confirm/save it.
    return index;
}

async Task<object> Describe(BrokerRequest request)
{
    InstrumentInstance instrument = FindInstrument(request);
    DriverConfig driver = runner.Find(instrument.DriverId);
    DriverResponse response = await runner.InvokeAsync(
        driver,
        new(ProtocolConstants.Version, Guid.NewGuid().ToString("N"), "describe", instrument),
        CancellationToken.None);
    if (!response.Success) throw new InvalidOperationException(response.Error);
    return response.Operations!;
}

async Task<object> Operate(BrokerRequest request)
{
    InstrumentInstance instrument = FindInstrument(request);
    if (string.IsNullOrWhiteSpace(request.OperationId)) throw new ArgumentException("operationId required");
    DriverConfig driver = runner.Find(instrument.DriverId);
    DriverResponse response = await runner.InvokeAsync(
        driver,
        new(ProtocolConstants.Version, Guid.NewGuid().ToString("N"), "generate", instrument, request.OperationId, request.Parameters),
        CancellationToken.None);
    if (!response.Success) throw new InvalidOperationException(response.Error);

    IReadOnlyList<GeneratedCommand> commands = response.Commands ?? [];
    ValidateCommands(driver, commands);

    var results = new List<object>();
    foreach (GeneratedCommand command in commands)
    {
        string? hardwareResponse = null;
        if (!request.DryRun)
        {
            LinuxGpib device = await GetGpib(instrument.Address);
            hardwareResponse = await device.ExecuteAsync(command.Text, command.ExpectsResponse, CancellationToken.None);
            int delay = Math.Max(
                command.DelayAfterMilliseconds,
                command.Category == "relay-switch" ? cfg.Security.MinimumRelayDelayMilliseconds : 0);
            if (delay > 0) await Task.Delay(delay);
        }
        results.Add(new { command.Text, command.ExpectsResponse, command.Category, response = hardwareResponse });
    }

    await audit.WriteAsync(new
    {
        type = "operation",
        instrument = instrument.Id,
        operation = request.OperationId,
        dryRun = request.DryRun,
        commands = commands.Select(x => x.Text),
    });
    return results;
}

void ValidateCommands(DriverConfig driver, IReadOnlyList<GeneratedCommand> commands)
{
    if (commands.Count > cfg.Security.MaxCommandsPerOperation) throw new InvalidOperationException("Too many commands");
    foreach (GeneratedCommand command in commands)
    {
        if (command.Text.Contains('\n') || command.Text.Contains('\r') || command.Text.Contains(';'))
            throw new InvalidOperationException("Unsafe command formatting");
        if (!driver.AllowedCategories.Contains(command.Category, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Command category denied");
        if (!driver.AllowedCommandPrefixes.Any(prefix => command.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Command prefix denied");
    }
}

InstrumentInstance FindInstrument(BrokerRequest request) => inventory.Find(request.InstrumentId ?? string.Empty);

async Task<LinuxGpib> GetGpib(InstrumentAddress address)
{
    string key = $"{address.BusId}:{address.PrimaryAddress}:{address.SecondaryAddress}";
    await gpibGate.WaitAsync();
    try
    {
        if (gpib.TryGetValue(key, out LinuxGpib? existing)) return existing;
        BusConfig bus = cfg.Buses.Single(x => x.Id == address.BusId);
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
    Console.WriteLine($"VXI web UI listening on http://{web.BindAddress}:{web.Port}/");
    while (true)
    {
        HttpListenerContext context = await http.GetContextAsync();
        _ = Task.Run(() => HandleHttp(context));
    }
}

async Task HandleHttp(HttpListenerContext context)
{
    try
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/")
        {
            await Write(context, "text/html; charset=utf-8", WebAssets.Page);
            return;
        }
        if (path == "/api/status")
        {
            await Json(context, new
            {
                status = "ready",
                drivers = cfg.Drivers,
                instruments = inventory.Snapshot(),
                buses = cfg.Buses,
                liveConfirmationPhrase = LiveConfirmationPhrase,
            });
            return;
        }
        if (path == "/api/discover" && context.Request.HttpMethod == "POST")
        {
            await Json(context, await Discover());
            return;
        }
        if (path == "/api/inventory" && context.Request.HttpMethod == "GET")
        {
            await Json(context, inventory.Snapshot());
            return;
        }
        if (path == "/api/inventory" && context.Request.HttpMethod == "POST")
        {
            string body = await ReadBody(context);
            List<InstrumentInstance> items = JsonSerializer.Deserialize<List<InstrumentInstance>>(body, JsonDefaults.Options)
                ?? throw new InvalidDataException("Invalid inventory JSON");
            foreach (InstrumentInstance item in items) runner.Find(item.DriverId);
            await inventory.ReplaceAsync(items);
            await audit.WriteAsync(new { type = "inventory-updated", count = items.Count });
            await Json(context, new { saved = true, count = items.Count });
            return;
        }
        if (path == "/api/operate" && context.Request.HttpMethod == "POST")
        {
            string body = await ReadBody(context);
            WebOperateRequest webRequest = JsonSerializer.Deserialize<WebOperateRequest>(body, JsonDefaults.Options)
                ?? throw new InvalidDataException("Invalid operation JSON");
            if (!webRequest.DryRun && !string.Equals(webRequest.Confirmation, LiveConfirmationPhrase, StringComparison.Ordinal))
                throw new InvalidOperationException($"Live relay operation requires exact confirmation phrase: {LiveConfirmationPhrase}");
            var request = new BrokerRequest(
                "operate",
                webRequest.InstrumentId,
                webRequest.OperationId,
                webRequest.Parameters,
                webRequest.DryRun);
            await Json(context, await Operate(request));
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

static async Task<string> ReadBody(HttpListenerContext context)
{
    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
    return await reader.ReadToEndAsync();
}

static async Task Json(HttpListenerContext context, object value) =>
    await Write(context, "application/json", JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));

static async Task Write(HttpListenerContext context, string contentType, string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    context.Response.ContentType = contentType;
    context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

static class WebAssets
{
public const string Page = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>VXI Commander</title>
<style>
:root{font-family:system-ui;color:#18212b;background:#f4f6f8}body{margin:0}.wrap{max-width:1250px;margin:auto;padding:24px}header{display:flex;justify-content:space-between;align-items:center}.card{background:white;border:1px solid #d8dee5;border-radius:12px;padding:18px;margin:16px 0;box-shadow:0 2px 8px #0000000b}button{padding:.65rem 1rem;border:1px solid #64748b;border-radius:8px;background:#fff;cursor:pointer}button.primary{background:#1d4ed8;color:white;border-color:#1d4ed8}button.danger{background:#991b1b;color:white;border-color:#991b1b}button:disabled{opacity:.5}pre{background:#111827;color:#e5e7eb;padding:14px;border-radius:8px;overflow:auto}.chassis{display:grid;grid-template-columns:repeat(13,minmax(70px,1fr));gap:8px}.slot{min-height:130px;border:2px solid #94a3b8;border-radius:8px;padding:8px;background:#f8fafc}.slot strong{display:block}.slot.controller{border-color:#2563eb}.slot.module{border-color:#059669}.slot.unknown{border-color:#d97706}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}.field label{font-size:.8rem;color:#475569}.field input,.field select{width:100%;box-sizing:border-box;padding:.55rem;border:1px solid #cbd5e1;border-radius:7px}table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #e2e8f0;padding:.5rem;text-align:left}.warn{background:#fff7ed;border-left:4px solid #ea580c;padding:12px}.ok{color:#047857}.bad{color:#b91c1c}@media(max-width:900px){.chassis{grid-template-columns:repeat(4,1fr)}.grid{grid-template-columns:1fr 1fr}}
</style>
</head>
<body><div class="wrap">
<header><div><h1>VXI Commander</h1><div id="status">Loading…</div></div><button class="primary" onclick="discover()">Discover hardware</button></header>
<div class="card"><h2>Detected chassis</h2><div id="chassis" class="chassis"></div><pre id="discovery">Not scanned yet.</pre></div>
<div class="card"><h2>Confirmed module inventory</h2><p>Discovery can identify logical addresses and the switchbox endpoint, but this E1406A firmware does not report every module model or physical slot. Confirm assignments here before live control.</p><button onclick="addRow()">Add module</button><button onclick="saveInventory()">Save inventory</button><div style="overflow:auto"><table><thead><tr><th>ID</th><th>Name</th><th>Driver</th><th>Model</th><th>PAD</th><th>SAD</th><th>Slot</th><th>LADDR</th><th>Card</th><th></th></tr></thead><tbody id="rows"></tbody></table></div><pre id="message"></pre></div>
<div class="card"><h2>Guarded relay test</h2><div class="warn"><strong>Use with RF power removed.</strong> Start with dry-run. For a live test, choose one known E1472A channel, type the confirmation phrase exactly, close it, verify software readback, then open/restore it.</div>
<div class="grid"><div class="field"><label>Instrument</label><select id="testInstrument"></select></div><div class="field"><label>Module (0-2)</label><input id="testModule" type="number" value="0" min="0" max="2"></div><div class="field"><label>Bank (0-5)</label><input id="testBank" type="number" value="0" min="0" max="5"></div><div class="field"><label>Channel in bank (0-3)</label><input id="testChannel" type="number" value="0" min="0" max="3"></div></div>
<p><button onclick="relay('query-channel',true)">Dry-run query</button><button onclick="relay('close-channel',true)">Dry-run close</button><button onclick="relay('open-channel',true)">Dry-run open</button></p>
<div class="field"><label>Live confirmation phrase</label><input id="confirmation" placeholder="SWITCH RELAY"></div>
<p><button class="danger" onclick="relay('close-channel',false)">LIVE close</button><button class="danger" onclick="relay('query-channel',false)">LIVE verify</button><button class="danger" onclick="relay('open-channel',false)">LIVE restore/open</button></p><pre id="relayResult"></pre></div>
<p>Local-only UI. From Windows: <code>ssh -L 8080:127.0.0.1:8080 user@rpi</code></p>
</div>
<script>
let drivers=[],buses=[],inventory=[];const q=s=>document.querySelector(s);const esc=s=>String(s??'').replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;');
async function init(){const s=await (await fetch('/api/status')).json();drivers=s.drivers;buses=s.buses;inventory=await (await fetch('/api/inventory')).json();q('#status').innerHTML=`Broker ready · ${drivers.length} drivers · ${inventory.length} configured modules`;inventory.forEach(addRow);refreshTestInstruments();renderChassis([],inventory)}
function driverOptions(v){return drivers.map(d=>`<option value="${esc(d.id)}" ${d.id===v?'selected':''}>${esc(d.id)}</option>`).join('')}
function addRow(x={id:`module-${q('#rows').children.length+1}`,friendlyName:'',driverId:drivers[0]?.id||'',manufacturer:'HP',model:'',enabled:true,manuallyAssigned:true,address:{busId:buses[0]?.id||'gpib0',primaryAddress:10,secondaryAddress:15,physicalSlot:null,logicalAddress:null,switchboxCardNumber:null}}){const tr=document.createElement('tr');tr.innerHTML=`<td><input data-k=id value="${esc(x.id)}"></td><td><input data-k=friendlyName value="${esc(x.friendlyName)}"></td><td><select data-k=driverId>${driverOptions(x.driverId)}</select></td><td><input data-k=model value="${esc(x.model)}"></td>${['primaryAddress','secondaryAddress','physicalSlot','logicalAddress','switchboxCardNumber'].map(k=>`<td><input type=number data-a=${k} value="${x.address[k]??''}"></td>`).join('')}<td><button onclick="this.closest('tr').remove()">Remove</button></td>`;q('#rows').appendChild(tr)}
function readInventory(){return [...q('#rows').children].map(tr=>{const o={enabled:true,manuallyAssigned:true,manufacturer:'HP',address:{busId:buses[0]?.id||'gpib0'}};tr.querySelectorAll('[data-k]').forEach(e=>o[e.dataset.k]=e.value);tr.querySelectorAll('[data-a]').forEach(e=>o.address[e.dataset.a]=e.value===''?null:Number(e.value));return o})}
async function saveInventory(){const items=readInventory();const r=await fetch('/api/inventory',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(items)});q('#message').textContent=await r.text();if(r.ok){inventory=items;refreshTestInstruments();renderChassis([],inventory)}}
async function discover(){q('#discovery').textContent='Scanning PAD 0-30 at SAD 0…';const r=await fetch('/api/discover',{method:'POST'});const data=await r.json();q('#discovery').textContent=JSON.stringify(data,null,2);renderChassis(data,inventory)}
function renderChassis(found,items){const slots=Array.from({length:13},(_,i)=>({slot:i}));items.forEach(i=>{if(i.address.physicalSlot!=null&&i.address.physicalSlot>=0&&i.address.physicalSlot<slots.length)slots[i.address.physicalSlot].item=i});const main=found[0];if(main)slots[0].detected={name:'E1406A',idn:main.identification};q('#chassis').innerHTML=slots.map(s=>{const x=s.item||s.detected;return `<div class="slot ${s.slot===0?'controller':x?'module':''}"><strong>Slot ${s.slot}</strong>${x?`<div>${esc(x.friendlyName||x.name||x.model||'Assigned module')}</div><small>${esc(x.driverId||x.idn||'')}</small>`:'<small>Unassigned</small>'}</div>`}).join('')}
function refreshTestInstruments(){q('#testInstrument').innerHTML=inventory.filter(x=>x.driverId==='hp.e1472a').map(x=>`<option value="${esc(x.id)}">${esc(x.friendlyName||x.id)}</option>`).join('')}
async function relay(operation,dryRun){const payload={instrumentId:q('#testInstrument').value,operationId:operation,dryRun,confirmation:q('#confirmation').value,parameters:{module:Number(q('#testModule').value),bank:Number(q('#testBank').value),channel:Number(q('#testChannel').value)}};const r=await fetch('/api/operate',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(payload)});q('#relayResult').textContent=await r.text()}
init();
</script></body></html>
""";
}

sealed record WebOperateRequest(
    string InstrumentId,
    string OperationId,
    Dictionary<string, JsonElement> Parameters,
    bool DryRun = true,
    string? Confirmation = null);

sealed record MainframeDiscoveryView(
    string BusId,
    int BoardIndex,
    int PrimaryAddress,
    string Identification,
    int? SwitchSecondaryAddress,
    string? SwitchIdentification,
    string? SwitchError,
    IReadOnlyList<DiscoveredModuleView> Modules,
    string RawConfiguration);

sealed record DiscoveredModuleView(
    int LogicalAddress,
    int? PhysicalSlot,
    int DeviceType,
    int ManufacturerId,
    string DeviceClass,
    string AddressSpace,
    string Status,
    string Description,
    int? SuggestedSwitchboxCardNumber);
