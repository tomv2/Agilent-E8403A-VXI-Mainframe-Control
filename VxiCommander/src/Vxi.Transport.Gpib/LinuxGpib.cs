using System.Runtime.InteropServices; using System.Text;
namespace Vxi.Transport.Gpib;
public sealed record GpibConnectionConfig(int BoardIndex,int PrimaryAddress,int SecondaryAddress,int TimeoutCode=13,bool SendEoi=true,int EosMode=0);
public sealed class LinuxGpib:IDisposable {
 [DllImport("libgpib.so.0")]static extern int ibdev(int b,int pad,int sad,int tmo,int eot,int eos);
 [DllImport("libgpib.so.0")]static extern int ibwrt(int ud,byte[] data,nuint count);
 [DllImport("libgpib.so.0")]static extern int ibrd(int ud,byte[] data,nuint count);
 [DllImport("libgpib.so.0")]static extern int ibonl(int ud,int v);
 [DllImport("libgpib.so.0")]static extern int ThreadIbsta(); [DllImport("libgpib.so.0")]static extern int ThreadIberr(); [DllImport("libgpib.so.0")]static extern long ThreadIbcntl();
 const int ERR=0x8000; readonly int _ud; readonly SemaphoreSlim _gate=new(1,1);
 public LinuxGpib(GpibConnectionConfig c){_ud=ibdev(c.BoardIndex,c.PrimaryAddress,c.SecondaryAddress,c.TimeoutCode,c.SendEoi?1:0,c.EosMode);Check("ibdev");}
 void Check(string op){if((ThreadIbsta()&ERR)!=0)throw new IOException($"{op} failed: iberr={ThreadIberr()}, ibcnt={ThreadIbcntl()}");}
 public async Task<string?> ExecuteAsync(string command,bool query,CancellationToken ct){await _gate.WaitAsync(ct);try{var bytes=Encoding.ASCII.GetBytes(command+"\n");ibwrt(_ud,bytes,(nuint)bytes.Length);Check("ibwrt");if(!query)return null;var b=new byte[65536];ibrd(_ud,b,(nuint)b.Length);Check("ibrd");return Encoding.ASCII.GetString(b,0,(int)ThreadIbcntl()).TrimEnd('\0','\r','\n');}finally{_gate.Release();}}
 public void Dispose(){ibonl(_ud,0);_gate.Dispose();}
}
