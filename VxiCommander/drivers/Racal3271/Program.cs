using Vxi.DriverSdk; using Vxi.Protocol; using System.Text.Json;
sealed class Driver:IVxiDriver {
 public DriverIdentity Identity=>new("racal.3271","Racal 3271 Controller","1.0.0",["3271"]);
 public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance i)=>[new("identify","Query identification",[]),new("reset","Reset instrument",[])];
 public IReadOnlyList<GeneratedCommand> Generate(InstrumentInstance i,string op,IReadOnlyDictionary<string,JsonElement> p)=>op switch{"identify"=>[new("*IDN?",true,"query")],"reset"=>[new("*RST",false,"reset")],_=>throw new ArgumentException("Unknown operation")};
}
return await DriverHost.RunAsync(new Driver());
