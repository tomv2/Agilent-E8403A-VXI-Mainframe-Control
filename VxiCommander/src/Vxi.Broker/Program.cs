using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vxi.Protocol;
using Vxi.Core;
using Vxi.Transport.Gpib;


const string WebPage="""
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>VXI Commander</title><style>body{font-family:system-ui;margin:2rem;max-width:1200px}button{padding:.55rem .9rem;margin:.3rem}table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:.4rem}input,select{width:100%;box-sizing:border-box}pre{background:#111;color:#eee;padding:1rem;overflow:auto}.note{background:#eef;padding:.8rem}</style></head><body>
<h1>VXI Commander</h1><p class="note">This page is local-only by default. From Windows use: <code>ssh -L 8080:127.0.0.1:8080 user@rpi</code>, then open <code>http://127.0.0.1:8080</code>.</p>
<button onclick="discover()">Discover GPIB</button><button onclick="addRow()">Add module manually</button><button onclick="save()">Save inventory</button>
<h2>Discovery</h2><pre id="discovery">Not scanned yet.</pre>
<h2>Module inventory</h2><table><thead><tr><th>ID</th><th>Name</th><th>Driver</th><th>Maker</th><th>Model</th><th>Bus</th><th>PAD</th><th>SAD</th><th>Slot</th><th>Logical</th><th>Card</th><th></th></tr></thead><tbody id="rows"></tbody></table><pre id="message"></pre>
<script>
let drivers=[],buses=[];const q=s=>document.querySelector(s);const esc=s=>String(s??'').replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;');
async function init(){let s=await (await fetch('/api/status')).json();drivers=s.drivers;buses=s.buses;let inv=await (await fetch('/api/inventory')).json();inv.forEach(addRow);}
function options(xs,val,key='id'){return xs.map(x=>`<option ${x[key]===val?'selected':''}>${esc(x[key])}</option>`).join('')}
function addRow(x={id:'module-'+(q('#rows').children.length+1),friendlyName:'',driverId:drivers[0]?.id||'',manufacturer:'',model:'',enabled:true,manuallyAssigned:true,address:{busId:buses[0]?.id||'gpib0',primaryAddress:0,secondaryAddress:0,physicalSlot:null,logicalAddress:null,switchboxCardNumber:null}}){let tr=document.createElement('tr');tr.innerHTML=`<td><input data-k=id value="${esc(x.id)}"></td><td><input data-k=friendlyName value="${esc(x.friendlyName)}"></td><td><select data-k=driverId>${options(drivers,x.driverId)}</select></td><td><input data-k=manufacturer value="${esc(x.manufacturer)}"></td><td><input data-k=model value="${esc(x.model)}"></td><td><select data-a=busId>${options(buses,x.address.busId)}</select></td>${['primaryAddress','secondaryAddress','physicalSlot','logicalAddress','switchboxCardNumber'].map(k=>`<td><input type=number data-a=${k} value="${x.address[k]??''}"></td>`).join('')}<td><button onclick="this.closest('tr').remove()">Remove</button></td>`;q('#rows').appendChild(tr)}
function inventory(){return [...q('#rows').children].map(tr=>{let o={enabled:true,manuallyAssigned:true,address:{}};tr.querySelectorAll('[data-k]').forEach(e=>o[e.dataset.k]=e.value);tr.querySelectorAll('[data-a]').forEach(e=>o.address[e.dataset.a]=e.value===''?null:(e.type==='number'?Number(e.value):e.value));return o})}
async function save(){let r=await fetch('/api/inventory',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(inventory())});q('#message').textContent=await r.text()}
async function discover(){q('#discovery').textContent='Scanning...';let r=await fetch('/api/discover',{method:'POST'});q('#discovery').textContent=JSON.stringify(await r.json(),null,2)}
init();
</script></body></html>
""";

string configPath=Environment.GetEnvironmentVariable("VXI_CONFIG")??"/etc/vxi-controller/appsettings.json";
var cfg=ConfigLoader.Load(configPath);
var runner=new DriverRunner(cfg);
var audit=new AuditLog(cfg.AuditLogPath);
var inventory=new InventoryStore(cfg.InventoryPath);
var gpib=new Dictionary<string,LinuxGpib>();
var gpibGate=new SemaphoreSlim(1,1);

if(File.Exists(cfg.SocketPath))File.Delete(cfg.SocketPath);
Directory.CreateDirectory(Path.GetDirectoryName(cfg.SocketPath)!);
var ep=new UnixDomainSocketEndPoint(cfg.SocketPath);
using var listener=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
listener.Bind(ep);
listener.Listen(20);
if(OperatingSystem.IsLinux())File.SetUnixFileMode(cfg.SocketPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.GroupWrite);
Console.WriteLine($"VXI broker listening on {cfg.SocketPath}");

if(cfg.Web?.Enabled==true)_=Task.Run(()=>RunWebAsync(cfg.Web));
while(true){var client=await listener.AcceptAsync();_=Task.Run(()=>HandleSocket(client));}

async Task HandleSocket(Socket s){await using var ns=new NetworkStream(s,true);using var r=new StreamReader(ns,Encoding.UTF8,leaveOpen:true);using var w=new StreamWriter(ns,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};try{var line=await r.ReadLineAsync();var br=JsonSerializer.Deserialize<BrokerRequest>(line!,JsonDefaults.Options)??throw new InvalidDataException("Invalid request");object data=await Dispatch(br);await w.WriteLineAsync(JsonSerializer.Serialize(new BrokerResponse(true,data),JsonDefaults.Options));}catch(Exception ex){await audit.WriteAsync(new{type="error",message=ex.Message});await w.WriteLineAsync(JsonSerializer.Serialize(new BrokerResponse(false,Error:ex.Message),JsonDefaults.Options));}}

async Task<object> Dispatch(BrokerRequest br)=>br.Action switch{
 "status"=>new{status="ready",drivers=cfg.Drivers.Length,instruments=inventory.Snapshot().Count,web=cfg.Web?.Enabled==true?$"http://{cfg.Web.BindAddress}:{cfg.Web.Port}/":null},
 "drivers"=>cfg.Drivers.Select(x=>new{x.Id,x.Executable}),
 "devices"=>inventory.Snapshot(),
 "discover"=>await Discover(),
 "describe"=>await Describe(br),
 "operate"=>await Operate(br),
 _=>throw new ArgumentException("Unknown action")};

async Task<object> Discover(){var results=new List<DiscoveredEndpoint>();foreach(var bus in cfg.Buses.Where(x=>x.AutoDiscover)){if(!bus.Transport.Equals("linux-gpib",StringComparison.OrdinalIgnoreCase))continue;var scan=await GpibDiscovery.ScanAsync(bus.BoardIndex,bus.TimeoutCode,CancellationToken.None);results.AddRange(scan.Select(x=>new DiscoveredEndpoint(bus.Id,x.BoardIndex,x.PrimaryAddress,x.SecondaryAddress,x.Identification,x.Kind,x.RawConfiguration,DateTimeOffset.UtcNow)));}await audit.WriteAsync(new{type="discovery",count=results.Count});return results;}

async Task<object> Describe(BrokerRequest br){var i=FindInstrument(br);var d=runner.Find(i.DriverId);var rr=await runner.InvokeAsync(d,new(ProtocolConstants.Version,Guid.NewGuid().ToString("N"),"describe",i),CancellationToken.None);if(!rr.Success)throw new InvalidOperationException(rr.Error);return rr.Operations!;}

async Task<object> Operate(BrokerRequest br){var i=FindInstrument(br);if(string.IsNullOrWhiteSpace(br.OperationId))throw new ArgumentException("operationId required");var d=runner.Find(i.DriverId);var rr=await runner.InvokeAsync(d,new(ProtocolConstants.Version,Guid.NewGuid().ToString("N"),"generate",i,br.OperationId,br.Parameters),CancellationToken.None);if(!rr.Success)throw new InvalidOperationException(rr.Error);var cmds=rr.Commands??[];if(cmds.Count>cfg.Security.MaxCommandsPerOperation)throw new InvalidOperationException("Too many commands");foreach(var c in cmds){if(c.Text.Contains('\n')||c.Text.Contains('\r')||c.Text.Contains(';'))throw new InvalidOperationException("Unsafe command formatting");if(!d.AllowedCategories.Contains(c.Category,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Command category denied");if(!d.AllowedCommandPrefixes.Any(p=>c.Text.StartsWith(p,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Command prefix denied");}
 var results=new List<object>();foreach(var c in cmds){string? response=null;if(!br.DryRun){var dev=await GetGpib(i.Address);response=await dev.ExecuteAsync(c.Text,c.ExpectsResponse,CancellationToken.None);int delay=Math.Max(c.DelayAfterMilliseconds,c.Category=="relay-switch"?cfg.Security.MinimumRelayDelayMilliseconds:0);if(delay>0)await Task.Delay(delay);}results.Add(new{c.Text,c.ExpectsResponse,c.Category,response});}await audit.WriteAsync(new{type="operation",instrument=i.Id,operation=br.OperationId,dryRun=br.DryRun,commands=cmds.Select(x=>x.Text)});return results;}

InstrumentInstance FindInstrument(BrokerRequest br)=>inventory.Find(br.InstrumentId??"");
async Task<LinuxGpib> GetGpib(InstrumentAddress a){string key=$"{a.BusId}:{a.PrimaryAddress}:{a.SecondaryAddress}";await gpibGate.WaitAsync();try{if(gpib.TryGetValue(key,out var existing))return existing;var bus=cfg.Buses.Single(x=>x.Id==a.BusId);var created=new LinuxGpib(new(bus.BoardIndex,a.PrimaryAddress,a.SecondaryAddress,bus.TimeoutCode));gpib[key]=created;return created;}finally{gpibGate.Release();}}

async Task RunWebAsync(WebConfig web){var h=new HttpListener();h.Prefixes.Add($"http://{web.BindAddress}:{web.Port}/");h.Start();Console.WriteLine($"VXI web UI listening on http://{web.BindAddress}:{web.Port}/");while(true){var ctx=await h.GetContextAsync();_=Task.Run(()=>HandleHttp(ctx));}}

async Task HandleHttp(HttpListenerContext ctx){try{string path=ctx.Request.Url?.AbsolutePath??"/";if(path=="/"){await Write(ctx,"text/html; charset=utf-8",WebPage);return;}if(path=="/api/status"){await Json(ctx,new{status="ready",drivers=cfg.Drivers,instruments=inventory.Snapshot(),buses=cfg.Buses});return;}if(path=="/api/discover"&&ctx.Request.HttpMethod=="POST"){await Json(ctx,await Discover());return;}if(path=="/api/inventory"&&ctx.Request.HttpMethod=="GET"){await Json(ctx,inventory.Snapshot());return;}if(path=="/api/inventory"&&ctx.Request.HttpMethod=="POST"){using var sr=new StreamReader(ctx.Request.InputStream,Encoding.UTF8);var body=await sr.ReadToEndAsync();var items=JsonSerializer.Deserialize<List<InstrumentInstance>>(body,JsonDefaults.Options)??throw new InvalidDataException("Invalid inventory JSON");foreach(var item in items)runner.Find(item.DriverId);await inventory.ReplaceAsync(items);await audit.WriteAsync(new{type="inventory-updated",count=items.Count,remote=ctx.Request.RemoteEndPoint?.ToString()});await Json(ctx,new{saved=true,count=items.Count});return;}ctx.Response.StatusCode=404;await Json(ctx,new{error="Not found"});}catch(Exception ex){ctx.Response.StatusCode=400;await Json(ctx,new{error=ex.Message});}}

static async Task Json(HttpListenerContext c,object value)=>await Write(c,"application/json",JsonSerializer.Serialize(value,new JsonSerializerOptions(JsonDefaults.Options){WriteIndented=true}));
static async Task Write(HttpListenerContext c,string type,string text){byte[] b=Encoding.UTF8.GetBytes(text);c.Response.ContentType=type;c.Response.ContentLength64=b.Length;await c.Response.OutputStream.WriteAsync(b);c.Response.Close();}
