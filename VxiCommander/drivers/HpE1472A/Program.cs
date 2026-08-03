using System.Text.Json;
using Vxi.DriverSdk;
using Vxi.Protocol;

return await DriverHost.RunAsync(new Driver());

sealed class Driver : IVxiDriver
{
    public DriverIdentity Identity => new(
        "hp.e1472a",
        "HP E1472A/E1474A RF Multiplexer",
        "1.2.0",
        ["E1472A", "E1474A"]);

    public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument) =>
    [
        new(
            "select",
            "Select multiplexer channel",
            [
                new(
                    "module",
                    "integer",
                    true,
                    0,
                    2,
                    Description:
                        "0 = base E1472A/E1474A; 1 or 2 = attached E1473A/E1475A expander."),
                new(
                    "channel",
                    "integer",
                    true,
                    0,
                    53,
                    Description:
                        "Bank/channel number: 00-03, 10-13, 20-23, 30-33, 40-43, or 50-53.")
            ],
            "Connects one input to the common connector in its bank."),

        new(
            "restore",
            "Restore bank default channel",
            [
                new("module", "integer", true, 0, 2),
                new(
                    "channel",
                    "integer",
                    true,
                    0,
                    53,
                    Description:
                        "Any valid channel in the target bank; the driver selects that bank's n0 channel.")
            ],
            "Restores the selected bank to channel n0, which is the power-on/reset state."),

        new(
            "query",
            "Query selected state",
            [
                new("module", "integer", true, 0, 2),
                new("channel", "integer", true, 0, 53)
            ],
            "Returns 1 when the specified channel is selected, otherwise 0.")
    ];

    public IReadOnlyList<GeneratedCommand> Generate(
        InstrumentInstance instrument,
        string operation,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int card = instrument.Address.SwitchboxCardNumber
            ?? throw new ArgumentException("switchboxCardNumber required");

        int module = Args.Int(parameters, "module", 0, 2);
        int channel = Args.Int(parameters, "channel", 0, 53);

        ValidateChannel(channel);

        string Address(int selectedChannel) =>
            $"{card:D2}{module:D2}{selectedChannel:D2}";

        int bankDefault = (channel / 10) * 10;

        return operation switch
        {
            "select" =>
            [
                new(
                    $"CLOS (@{Address(channel)})",
                    Category: "relay-switch",
                    DelayAfterMilliseconds: 20)
            ],

            "restore" =>
            [
                new(
                    $"CLOS (@{Address(bankDefault)})",
                    Category: "relay-switch",
                    DelayAfterMilliseconds: 20)
            ],

            "query" =>
            [
                new(
                    $"CLOS? (@{Address(channel)})",
                    ExpectsResponse: true,
                    Category: "relay-switch")
            ],

            _ => throw new ArgumentException("Unknown operation")
        };
    }

    private static void ValidateChannel(int channel)
    {
        int bank = channel / 10;
        int input = channel % 10;

        if (bank is < 0 or > 5 || input is < 0 or > 3)
        {
            throw new ArgumentException(
                "Invalid RF multiplexer channel. Use 00-03, 10-13, 20-23, " +
                "30-33, 40-43, or 50-53.");
        }
    }
}
