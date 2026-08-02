using System.Text.Json; using Vxi.Protocol;
namespace Vxi.DriverSdk;
public interface IVxiDriver {
 DriverIdentity Identity { get; }
 IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument);
 IReadOnlyList<GeneratedCommand> Generate(InstrumentInstance instrument,string operationId,IReadOnlyDictionary<string,JsonElement> parameters);
}
public static class Args {
 public static int Int(IReadOnlyDictionary<string,JsonElement> p,string n,int min,int max){if(!p.TryGetValue(n,out var e)||!e.TryGetInt32(out var v)||v<min||v>max)throw new ArgumentException($"{n} must be {min}..{max}");return v;}
 public static double Number(IReadOnlyDictionary<string,JsonElement> p,string n,double min,double max){if(!p.TryGetValue(n,out var e)||!e.TryGetDouble(out var v)||v<min||v>max)throw new ArgumentException($"{n} must be {min}..{max}");return v;}
}
public static class DriverHost {
 public static async Task<int> RunAsync(IVxiDriver driver){
  string? line=await Console.In.ReadLineAsync(); if(string.IsNullOrWhiteSpace(line)) return 2;
  DriverRequest? req=null; try { req=JsonSerializer.Deserialize<DriverRequest>(line,JsonDefaults.Options)??throw new InvalidDataException("Empty request"); if(req.ProtocolVersion!=ProtocolConstants.Version)throw new InvalidOperationException("Unsupported protocol version");
   DriverResponse result=req.Action switch {
    "identity"=>new(ProtocolConstants.Version,req.RequestId,true,Driver:driver.Identity),
    "describe" when req.Instrument is not null=>new(ProtocolConstants.Version,req.RequestId,true,Driver:driver.Identity,Operations:driver.Describe(req.Instrument)),
    "generate" when req.Instrument is not null && req.OperationId is not null=>new(ProtocolConstants.Version,req.RequestId,true,Commands:driver.Generate(req.Instrument,req.OperationId,req.Parameters??new())),
    _=>throw new InvalidOperationException("Invalid driver action")};
   await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result,JsonDefaults.Options)); return 0;
  } catch(Exception ex){await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new DriverResponse(ProtocolConstants.Version,req?.RequestId??"unknown",false,Error:ex.Message),JsonDefaults.Options)); return 1;}
 }
}
