using System.Text.Json;
using Vxi.DriverSdk;
using Vxi.Protocol;

return await DriverHost.RunAsync(new Driver());

sealed class Driver : IVxiDriver
{
    public DriverIdentity Identity => new(
        "hp.e1368a",
        "HP E1368A 18 GHz Microwave Switch",
        "1.1.0",
        ["E1368A"]);

    public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument) =>
    [
        new(
            "close",
            "Close RF switch",
            [new("switch", "integer", true, 0, 2)],
            "Connects port 2 to common on switch 00, 01, or 02."),

        new(
            "open",
            "Open RF switch",
            [new("switch", "integer", true, 0, 2)],
            "Connects port 1 to common on switch 00, 01, or 02."),

        new(
            "query-closed",
            "Query closed state",
            [new("switch", "integer", true, 0, 2)],
            "Returns 1 when the close command is active, otherwise 0."),

        new(
            "query-open",
            "Query open state",
            [new("switch", "integer", true, 0, 2)],
            "Returns 1 when the open command is active, otherwise 0.")
    ];

    public IReadOnlyList<GeneratedCommand> Generate(
        InstrumentInstance instrument,
        string operation,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int card = instrument.Address.SwitchboxCardNumber
            ?? throw new ArgumentException("switchboxCardNumber required");

        int channel = Args.Int(parameters, "switch", 0, 2);
        string address = $"{card:D2}{channel:D2}";

        return operation switch
        {
            "close" =>
            [
                new(
                    $"CLOS (@{address})",
                    Category: "relay-switch",
                    DelayAfterMilliseconds: 20)
            ],

            "open" =>
            [
                new(
                    $"OPEN (@{address})",
                    Category: "relay-switch",
                    DelayAfterMilliseconds: 20)
            ],

            "query-closed" =>
            [
                new(
                    $"CLOS? (@{address})",
                    ExpectsResponse: true,
                    Category: "relay-switch")
            ],

            "query-open" =>
            [
                new(
                    $"OPEN? (@{address})",
                    ExpectsResponse: true,
                    Category: "relay-switch")
            ],

            _ => throw new ArgumentException("Unknown operation")
        };
    }
}
