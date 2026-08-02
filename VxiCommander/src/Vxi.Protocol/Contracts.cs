using System.Text.Json;
using System.Text.Json.Serialization;
namespace Vxi.Protocol;
public static class ProtocolConstants { public const int Version = 1; }
public sealed record DriverIdentity(string Id,string Name,string Version,IReadOnlyList<string> SupportedModels);
public sealed record ParameterDescriptor(string Name,string Type,bool Required=true,double? Minimum=null,double? Maximum=null,IReadOnlyList<string>? Choices=null,string? Description=null);
public sealed record OperationDescriptor(string Id,string Name,IReadOnlyList<ParameterDescriptor> Parameters,string? Description=null);
public sealed record InstrumentAddress(string ConnectionId,int? PhysicalSlot=null,int? LogicalAddress=null,int? SwitchboxCardNumber=null);
public sealed record InstrumentInstance(string Id,string FriendlyName,string DriverId,string Manufacturer,string Model,InstrumentAddress Address);
public sealed record DriverRequest(int ProtocolVersion,string RequestId,string Action,InstrumentInstance? Instrument=null,string? OperationId=null,Dictionary<string,JsonElement>? Parameters=null);
public sealed record GeneratedCommand(string Text,bool ExpectsResponse=false,string Category="instrument",int DelayAfterMilliseconds=0);
public sealed record DriverResponse(int ProtocolVersion,string RequestId,bool Success,DriverIdentity? Driver=null,IReadOnlyList<OperationDescriptor>? Operations=null,IReadOnlyList<GeneratedCommand>? Commands=null,string? Error=null);
public sealed record BrokerRequest(string Action,string? InstrumentId=null,string? OperationId=null,Dictionary<string,JsonElement>? Parameters=null,bool DryRun=true);
public sealed record BrokerResponse(bool Success,object? Data=null,string? Error=null);
public static class JsonDefaults { public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web){WriteIndented=false,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull}; }
