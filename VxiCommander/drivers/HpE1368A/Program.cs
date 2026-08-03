using System.Text.Json;
using Vxi.DriverSdk;
using Vxi.Protocol;

return await DriverHost.RunAsync(new Driver());

sealed class Driver : IVxiDriver
{
    public DriverIdentity Identity => new("hp.e1368a", "HP E1368A Microwave Switch", "1.1.0", ["E1368A"]);

    public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument) =>
    [
        new("query-channel", "Query channel closure", ChannelParameters(), "Software readback only."),
        new("close-channel", "Close channel", ChannelParameters()),
        new("open-channel", "Open channel", ChannelParameters()),
    ];

    public IReadOnlyList<GeneratedCommand> Generate(
        InstrumentInstance instrument,
        string operation,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int card = instrument.Address.SwitchboxCardNumber ?? throw new ArgumentException("switchboxCardNumber required");
        int channel = Args.Int(parameters, "channel", 0, 99);
        string address = $"{card:D2}{channel:D2}";

        return operation switch
        {
            "query-channel" => [new($"CLOS? (@{address})", true, "relay-query")],
            "close-channel" => [new($"CLOS (@{address})", false, "relay-switch", 20)],
            "open-channel" => [new($"OPEN (@{address})", false, "relay-switch", 20)],
            _ => throw new ArgumentException("Unknown operation"),
        };
    }

    private static IReadOnlyList<ParameterDescriptor> ChannelParameters() =>
    [
        new("channel", "integer", true, 0, 99, Description: "Use the channel number from the E1368A switch/relay wiring map."),
    ];
}
