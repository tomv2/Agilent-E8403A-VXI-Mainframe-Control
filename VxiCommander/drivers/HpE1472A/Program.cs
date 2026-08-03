using System.Text.Json;
using Vxi.DriverSdk;
using Vxi.Protocol;

return await DriverHost.RunAsync(new Driver());

sealed class Driver : IVxiDriver
{
    public DriverIdentity Identity => new("hp.e1472a", "HP E1472A RF Multiplexer", "1.1.0", ["E1472A"]);

    public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument) =>
    [
        new("query-channel", "Query channel closure", ChannelParameters(), "Software readback only; does not detect a failed relay."),
        new("close-channel", "Close/select channel", ChannelParameters(), "Connects the selected channel to its bank common."),
        new("open-channel", "Open channel", ChannelParameters(), "Opens the specified channel."),
    ];

    public IReadOnlyList<GeneratedCommand> Generate(
        InstrumentInstance instrument,
        string operation,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int card = instrument.Address.SwitchboxCardNumber ?? throw new ArgumentException("switchboxCardNumber required");
        int module = Args.Int(parameters, "module", 0, 2);
        int bank = Args.Int(parameters, "bank", 0, 5);
        int channelInBank = Args.Int(parameters, "channel", 0, 3);
        int channel = bank * 10 + channelInBank;
        string address = $"{card:D2}{module:D2}{channel:D2}";

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
        new("module", "integer", true, 0, 2, Description: "00 is the E1472A; 01/02 are optional expanders."),
        new("bank", "integer", true, 0, 5),
        new("channel", "integer", true, 0, 3, Description: "Channel within the selected bank."),
    ];
}
