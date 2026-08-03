using Vxi.DriverSdk; using Vxi.Protocol; using System.Text.Json;
return await DriverHost.RunAsync(new Driver());
sealed class Driver:IVxiDriver {
 public DriverIdentity Identity=>new("hp.e1472a","HP E1472A RF Multiplexer","1.0.0",["E1472A"]);
 public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance i)=>[
  new("select","Select multiplexer channel",[new("module","integer",true,0,9),new("channel","integer",true,0,99)]),
  new("open","Open multiplexer channel",[new("module","integer",true,0,9),new("channel","integer",true,0,99)])];
 public IReadOnlyList<GeneratedCommand> Generate(InstrumentInstance i,string op,IReadOnlyDictionary<string,JsonElement> p){int card=i.Address.SwitchboxCardNumber??throw new ArgumentException("switchboxCardNumber required");int m=Args.Int(p,"module",0,9),c=Args.Int(p,"channel",0,99);string addr=$"{card:D2}{m:D2}{c:D2}";return op switch{"select"=>[new($"CLOS (@{addr})",Category:"relay-switch",DelayAfterMilliseconds:20)],"open"=>[new($"OPEN (@{addr})",Category:"relay-switch",DelayAfterMilliseconds:20)],_=>throw new ArgumentException("Unknown operation")};}
}
