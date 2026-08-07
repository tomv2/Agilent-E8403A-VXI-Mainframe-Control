using System.Runtime.InteropServices;
using System.Text;
namespace Vxi.Transport.Gpib;

public sealed record GpibConnectionConfig(int BoardIndex,int PrimaryAddress,int SecondaryAddress,int TimeoutCode=13,bool SendEoi=true,int EosMode=0);
public sealed class LinuxGpib:IDisposable {
 [DllImport("libgpib.so.0")]static extern int ibdev(int b,int pad,int sad,int tmo,int eot,int eos);
 [DllImport("libgpib.so.0")]static extern int ibwrt(int ud,byte[] data,nuint count);
 [DllImport("libgpib.so.0")]static extern int ibrd(int ud,byte[] data,nuint count);
 [DllImport("libgpib.so.0")]static extern int ibonl(int ud,int v);
 [DllImport("libgpib.so.0")]static extern int ibln(int board,int pad,int sad,out short listen);
 [DllImport("libgpib.so.0")]static extern int ThreadIbsta(); [DllImport("libgpib.so.0")]static extern int ThreadIberr(); [DllImport("libgpib.so.0")]static extern long ThreadIbcntl();
 const int ERR=0x8000;
 public static int EncodeSecondaryAddress(int secondary){
  if(secondary<0||secondary>30)throw new ArgumentOutOfRangeException(nameof(secondary));
  return 0x60+secondary;
 }
 readonly int _ud; readonly SemaphoreSlim _gate=new(1,1);
 public LinuxGpib(GpibConnectionConfig c){_ud=ibdev(c.BoardIndex,c.PrimaryAddress,EncodeSecondaryAddress(c.SecondaryAddress),c.TimeoutCode,c.SendEoi?1:0,c.EosMode);Check("ibdev");}
 void Check(string op){if((ThreadIbsta()&ERR)!=0)throw new IOException($"{op} failed: iberr={ThreadIberr()}, ibcnt={ThreadIbcntl()}");}
 public async Task<string?> ExecuteAsync(string command,bool query,CancellationToken ct){await _gate.WaitAsync(ct);try{var bytes=Encoding.ASCII.GetBytes(command+"\n");ibwrt(_ud,bytes,(nuint)bytes.Length);Check("ibwrt");if(!query)return null;var b=new byte[65536];ibrd(_ud,b,(nuint)b.Length);Check("ibrd");return Encoding.ASCII.GetString(b,0,(int)ThreadIbcntl()).TrimEnd('\0','\r','\n');}finally{_gate.Release();}}
 public static bool IsListener(int board,int primary,int secondary){int rc=ibln(board,primary,EncodeSecondaryAddress(secondary),out short listen);if(rc<0||(ThreadIbsta()&ERR)!=0)return false;return listen!=0;}
 public void Dispose(){ibonl(_ud,0);_gate.Dispose();}
}

public static class GpibDiscovery {
 public static async Task<IReadOnlyList<GpibProbeResult>> ScanAsync(int board,int timeoutCode,CancellationToken ct){var found=new List<GpibProbeResult>();for(int primary=1;primary<=30;primary++){ct.ThrowIfCancellationRequested();if(!LinuxGpib.IsListener(board,primary,0))continue;for(int secondary=0;secondary<=30;secondary++){ct.ThrowIfCancellationRequested();if(secondary>0&&!LinuxGpib.IsListener(board,primary,secondary))continue;string? idn=null,config=null;string? dlis = null;
string? information = null;

try
{
    using var dev = new LinuxGpib(
        new(board, primary, secondary, timeoutCode));

    dlis = await dev.ExecuteAsync(
        "VXI:CONF:DLIS?",
        true,
        ct);

    information = await dev.ExecuteAsync(
        "VXI:CONF:INF:ALL?",
        true,
        ct);
}
catch
{
}

if (!string.IsNullOrWhiteSpace(dlis) ||
    !string.IsNullOrWhiteSpace(information))
{
    config =
        $"DLIS:{dlis ?? ""}\n" +
        $"INF:{information ?? ""}";
}
   string kind=Classify(idn,secondary);if(kind=="e1406a-controller"){try{using var dev=new LinuxGpib(new(board,primary,secondary,timeoutCode));config=await dev.ExecuteAsync("VXI:CONF:INF:ALL?",true,ct);}catch{}}
   found.Add(new(board,primary,secondary,idn,kind,config));}}
 return found;}
 static string Classify(string? idn,int secondary){string s=idn??"";if(s.Contains("E1406",StringComparison.OrdinalIgnoreCase))return "e1406a-controller";if(s.Contains("RACAL",StringComparison.OrdinalIgnoreCase)||s.Contains("3271",StringComparison.OrdinalIgnoreCase))return "module-endpoint";if(s.Contains("SWITCH",StringComparison.OrdinalIgnoreCase))return "switchbox";return secondary==0?"gpib-device":"instrument-endpoint";}
}
public sealed record GpibProbeResult(int BoardIndex,int PrimaryAddress,int SecondaryAddress,string? Identification,string Kind,string? RawConfiguration);
