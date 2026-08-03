using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vxi.Protocol;
namespace Vxi.Core;

public sealed record BusConfig(string Id,string Transport,int BoardIndex=0,bool AutoDiscover=true,int TimeoutCode=11);
public sealed record WebConfig(bool Enabled=true,string BindAddress="127.0.0.1",int Port=8080);
public sealed record DriverConfig(string Id,string Directory,string Executable,string[] AllowedCommandPrefixes,string[] AllowedCategories,int TimeoutMilliseconds=5000,string? Sha256=null);
public sealed record SecurityConfig(bool RequireHashes=true,int MaxCommandsPerOperation=8,int MinimumRelayDelayMilliseconds=20);
public sealed record AppConfig(string SocketPath,string DriverRoot,string AuditLogPath,string InventoryPath,BusConfig[] Buses,DriverConfig[] Drivers,SecurityConfig Security,WebConfig? Web=null);

public static class ConfigLoader {
 public static AppConfig Load(string path)=>JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path),JsonDefaults.Options)??throw new InvalidDataException("Invalid config");
}

public sealed class InventoryStore {
 readonly string _path; readonly SemaphoreSlim _gate=new(1,1); readonly object _sync=new(); List<InstrumentInstance> _items;
 public InventoryStore(string path){_path=path;Directory.CreateDirectory(Path.GetDirectoryName(path)!);_items=Load();}
 List<InstrumentInstance> Load(){if(!File.Exists(_path))return [];return JsonSerializer.Deserialize<List<InstrumentInstance>>(File.ReadAllText(_path),JsonDefaults.Options)??[];}
 public IReadOnlyList<InstrumentInstance> Snapshot(){lock(_sync)return _items.ToArray();}
 public InstrumentInstance Find(string id)=>Snapshot().SingleOrDefault(x=>x.Id==id&&x.Enabled)??throw new ArgumentException("Unknown or disabled instrument");
 public async Task ReplaceAsync(IEnumerable<InstrumentInstance> items){await _gate.WaitAsync();try{var next=items.ToList();Validate(next);string tmp=_path+".tmp";await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(next,new JsonSerializerOptions(JsonDefaults.Options){WriteIndented=true}));File.Move(tmp,_path,true);lock(_sync)_items=next;}finally{_gate.Release();}}
 static void Validate(List<InstrumentInstance> xs){if(xs.GroupBy(x=>x.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("Duplicate instrument id");foreach(var x in xs){if(string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.DriverId))throw new InvalidDataException("Instrument id and driverId are required");if(x.Address.PrimaryAddress is <0 or >30||x.Address.SecondaryAddress is <0 or >30)throw new InvalidDataException("GPIB addresses must be 0..30");}}
}

public sealed class DriverRunner {
 readonly AppConfig _cfg; public DriverRunner(AppConfig c)=>_cfg=c;
 public DriverConfig Find(string id)=>_cfg.Drivers.Single(x=>x.Id==id);
 public async Task<DriverResponse> InvokeAsync(DriverConfig d,DriverRequest req,CancellationToken ct){string dir=Path.Combine(_cfg.DriverRoot,d.Directory),exe=Path.Combine(dir,d.Executable);if(!File.Exists(exe))throw new FileNotFoundException("Driver executable missing",exe);if(_cfg.Security.RequireHashes){if(string.IsNullOrWhiteSpace(d.Sha256))throw new InvalidOperationException($"No hash configured for {d.Id}");string hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(exe,ct)));if(!hash.Equals(d.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Hash mismatch for {d.Id}");}
  var psi=new ProcessStartInfo(exe){WorkingDirectory=dir,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};psi.Environment.Clear();psi.Environment["LANG"]="C";using var p=Process.Start(psi)??throw new InvalidOperationException("Unable to launch driver");await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req,JsonDefaults.Options));p.StandardInput.Close();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(d.TimeoutMilliseconds);string line=await p.StandardOutput.ReadLineAsync(timeout.Token)??throw new InvalidDataException("Driver returned no response");await p.WaitForExitAsync(timeout.Token);return JsonSerializer.Deserialize<DriverResponse>(line,JsonDefaults.Options)??throw new InvalidDataException("Invalid driver response"); }
}

public sealed class AuditLog { readonly string _path; readonly SemaphoreSlim _g=new(1,1); string _last="GENESIS"; public AuditLog(string p){_path=p;Directory.CreateDirectory(Path.GetDirectoryName(p)!);} public async Task WriteAsync(object e){await _g.WaitAsync();try{string payload=JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,previous=_last,@event=e},JsonDefaults.Options);_last=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));await File.AppendAllTextAsync(_path,JsonSerializer.Serialize(new{hash=_last,payload},JsonDefaults.Options)+"\n");}finally{_g.Release();}} }
