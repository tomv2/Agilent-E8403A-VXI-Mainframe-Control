using System.Text.Json;
using Vxi.DriverSdk;
using Vxi.Protocol;

return await DriverHost.RunAsync(new Driver());

sealed class Driver : IVxiDriver
{
    public DriverIdentity Identity => new(
        "hp.e1472a",
        "HP E1472A RF Multiplexer",
        "1.3.0",
        ["E1472A"]);

    public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument) =>
    [
        new("select", "Select multiplexer channel",
            [ModuleParameter(), ChannelParameter()],
            "Connects one input to COMMON in its bank."),
        new("restore", "Restore bank default channel",
            [ModuleParameter(), ChannelParameter()],
            "Selects channel x0 for the requested bank."),
        new("query", "Query selected state",
            [ModuleParameter(), ChannelParameter()]),
        new("query-open", "Query open state",
            [ModuleParameter(), ChannelParameter()]),
        new("query-bank", "Query complete bank",
            [ModuleParameter(), new("bank", "integer", true, 0, 5)],
            "Returns four comma-separated closure states."),
        new("query-module", "Query complete module",
            [ModuleParameter()],
            "Returns closure states for all 24 inputs."),
        new("identify", "Query switchbox identification", []),
        new("card-type", "Query card model and revision", []),
        new("card-description", "Query card description", []),
        new("card-options", "Query installed E1473A expanders", []),
        new("system-error", "Read next switchbox error", []),
        new("clear-status", "Clear status and error queue", []),
        new("self-test", "Run switchbox self-test", []),
        new("operation-complete", "Wait for operation complete", []),
        new("reset-card", "Reset this multiplexer card", [],
            "Returns all banks on this card to channel x0."),
        new("reset-switchbox", "Reset complete switchbox", [],
            "Returns all banks on every card to channel x0. This is disruptive."),
        new("save-state", "Save switchbox state",
            [new("location", "integer", true, 0, 9)]),
        new("recall-state", "Recall switchbox state",
            [new("location", "integer", true, 0, 9)],
            "Recalling a state changes relay positions.")
    ];

    public IReadOnlyList<GeneratedCommand> Generate(
        InstrumentInstance instrument,
        string operation,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int card = instrument.Address.SwitchboxCardNumber
            ?? throw new ArgumentException("switchboxCardNumber required");

        string Address(int module, int channel)
            => $"{card:D2}{module:D2}{channel:D2}";

        GeneratedCommand Query(string text, string category = "instrument-query")
            => new(text, true, category);

        GeneratedCommand Write(string text, string category, int delay = 0)
            => new(text, false, category, DelayAfterMilliseconds: delay);

        return operation switch
        {
            "select" =>
                [Write($"CLOS (@{Address(Module(parameters), Channel(parameters))})",
                    "relay-switch", 20)],
            "restore" =>
                [Write($"CLOS (@{Address(Module(parameters),
                    BankDefault(Channel(parameters)))})",
                    "relay-switch", 20)],
            "query" =>
                [Query($"CLOS? (@{Address(Module(parameters),
                    Channel(parameters))})", "relay-query")],
            "query-open" =>
                [Query($"OPEN? (@{Address(Module(parameters),
                    Channel(parameters))})", "relay-query")],
            "query-bank" =>
                [Query($"CLOS? (@{BankAddressList(card,
                    Module(parameters),
                    Args.Int(parameters, "bank", 0, 5))})",
                    "relay-query")],
            "query-module" =>
                [Query($"CLOS? (@{ModuleAddressList(card, Module(parameters))})",
                    "relay-query")],
            "identify" => [Query("*IDN?")],
            "card-type" => [Query($"SYST:CTYP? {card}")],
            "card-description" => [Query($"SYST:CDES? {card}")],
            "card-options" => [Query($"SYST:COPT? {card}")],
            "system-error" => [Query("SYST:ERR?", "diagnostic")],
            "clear-status" => [Write("*CLS", "diagnostic")],
            "self-test" => [Query("*TST?", "diagnostic")],
            "operation-complete" => [Query("*OPC?", "diagnostic")],
            "reset-card" => [Write($"SYST:CPON {card}", "reset", 250)],
            "reset-switchbox" => [Write("*RST", "reset", 500)],
            "save-state" =>
                [Write($"*SAV {Args.Int(parameters, "location", 0, 9)}",
                    "state-management")],
            "recall-state" =>
                [Write($"*RCL {Args.Int(parameters, "location", 0, 9)}",
                    "state-management", 250)],
            _ => throw new ArgumentException($"Unknown operation: {operation}")
        };
    }

    private static ParameterDescriptor ModuleParameter() =>
        new("module", "integer", true, 0, 2,
            Description: "0 = E1472A; 1 or 2 = E1473A expander.");

    private static ParameterDescriptor ChannelParameter() =>
        new("channel", "integer", true, 0, 53,
            Description:
                "Valid: 00-03, 10-13, 20-23, 30-33, 40-43, 50-53.");

    private static int Module(
        IReadOnlyDictionary<string, JsonElement> parameters)
        => Args.Int(parameters, "module", 0, 2);

    private static int Channel(
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int channel = Args.Int(parameters, "channel", 0, 53);

        if (channel % 10 > 3)
            throw new ArgumentException(
                "Valid channels end in 0, 1, 2, or 3.");

        return channel;
    }

    private static int BankDefault(int channel) => (channel / 10) * 10;

    private static string BankAddressList(int card, int module, int bank)
    {
        int first = bank * 10;
        return string.Join(",",
            Enumerable.Range(0, 4)
                .Select(input => $"{card:D2}{module:D2}{first + input:D2}"));
    }

    private static string ModuleAddressList(int card, int module) =>
        string.Join(",",
            Enumerable.Range(0, 6)
                .SelectMany(bank =>
                    Enumerable.Range(0, 4)
                        .Select(input =>
                            $"{card:D2}{module:D2}{bank * 10 + input:D2}")));
}
