using Vxi.DriverSdk; using Vxi.Protocol; using System.Text.Json;
sealed class Driver:IVxiDriver {
 public DriverIdentity Identity=>new("hp.e1368a","HP E1368A Microwave Switch","1.0.0",["E1368A"]);
 public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance i)=>[new("select-port","Select SPDT port",[new("switch","integer",true,1,3),new("port","integer",true,1,2)])];
 public IReadOnlyList<GeneratedCommand> Generate(InstrumentInstance i,string op,IReadOnlyDictionary<string,JsonElement> p){if(op!="select-port")throw new ArgumentException("Unknown operation");int card=i.Address.SwitchboxCardNumber??throw new ArgumentException("switchboxCardNumber required");int sw=Args.Int(p,"switch",1,3),port=Args.Int(p,"port",1,2);string addr=$"{card:D2}{sw:D2}";string cmd=port==2?$"CLOS (@{addr})":$"OPEN (@{addr})";return [new(cmd,Category:"relay-switch",DelayAfterMilliseconds:20)];}
}
return await DriverHost.RunAsync(new Driver());
