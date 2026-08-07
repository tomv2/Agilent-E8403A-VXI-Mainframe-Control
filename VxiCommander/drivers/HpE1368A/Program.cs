using System.Text.Json;
using Vxi.DriverSdk;
using Vxi.Protocol;

return await DriverHost.RunAsync(new Driver());

sealed class Driver : IVxiDriver
{
    public DriverIdentity Identity => new(
        "hp.e1368a",
        "HP E1368A Microwave Switch",
        "1.2.0",
        ["E1368A"]);

    public IReadOnlyList<OperationDescriptor> Describe(InstrumentInstance instrument) =>
    [
        new("close", "Connect Port 2",
            [new("switch", "integer", true, 0, 2)],
            "Connects Port 2 to COMMON on switch A, B, or C."),
        new("open", "Connect Port 1",
            [new("switch", "integer", true, 0, 2)],
            "Connects Port 1 to COMMON on switch A, B, or C."),
        new("query-closed", "Query Port 2 state",
            [new("switch", "integer", true, 0, 2)]),
        new("query-open", "Query Port 1 state",
            [new("switch", "integer", true, 0, 2)]),
        new("identify", "Query switchbox identification", []),
        new("card-type", "Query card model and revision", []),
        new("card-description", "Query card description", []),
        new("system-error", "Read next switchbox error", []),
        new("clear-status", "Clear status and error queue", []),
        new("self-test", "Run switchbox self-test", []),
        new("operation-complete", "Wait for operation complete", []),
        new("reset-card", "Reset this E1368A card", [],
            "Returns this card to its power-on state with all channels open."),
        new("reset-switchbox", "Reset complete switchbox", [],
            "Resets every card in the switchbox. This is disruptive."),
        new("query-all", "Query all three switches", [],
            "Returns Port 2 closure state for switches A, B, and C."),
        new("set-scan", "Define scan list",
            [
                new("firstSwitch", "integer", true, 0, 2),
                new("lastSwitch", "integer", true, 0, 2)
            ]),
        new("set-trigger-source", "Set scan trigger source",
            [new("source", "string", true,
                Description: "BUS, EXT, HOLD, or IMM")]),
        new("query-trigger-source", "Query scan trigger source", []),
        new("set-arm-count", "Set scan count",
            [new("count", "integer", true, 1, 32767)]),
        new("query-arm-count", "Query scan count", []),
        new("initiate-scan", "Start scan", []),
        new("abort-scan", "Abort scan", []),
        new("bus-trigger", "Issue bus trigger", []),
        new("query-scan-complete", "Query scan completion status", [])
    ];

    public IReadOnlyList<GeneratedCommand> Generate(
        InstrumentInstance instrument,
        string operation,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int card = instrument.Address.SwitchboxCardNumber
            ?? throw new ArgumentException("switchboxCardNumber required");

        string Address(int channel) => $"{card:D2}{channel:D2}";
        int Switch() => Args.Int(parameters, "switch", 0, 2);

        GeneratedCommand Query(string text, string category = "instrument-query")
            => new(text, true, category);

        GeneratedCommand Write(string text, string category, int delay = 0)
            => new(text, false, category, DelayAfterMilliseconds: delay);

        return operation switch
        {
            "close" => [Write($"CLOS (@{Address(Switch())})", "relay-switch", 20)],
            "open" => [Write($"OPEN (@{Address(Switch())})", "relay-switch", 20)],
            "query-closed" => [Query($"CLOS? (@{Address(Switch())})", "relay-query")],
            "query-open" => [Query($"OPEN? (@{Address(Switch())})", "relay-query")],
            "identify" => [Query("*IDN?")],
            "card-type" => [Query($"SYST:CTYP? {card}")],
            "card-description" => [Query($"SYST:CDES? {card}")],
            "system-error" => [Query("SYST:ERR?", "diagnostic")],
            "clear-status" => [Write("*CLS", "diagnostic")],
            "self-test" => [Query("*TST?", "diagnostic")],
            "operation-complete" => [Query("*OPC?", "diagnostic")],
            "reset-card" => [Write($"SYST:CPON {card}", "reset", 100)],
            "reset-switchbox" => [Write("*RST", "reset", 250)],
            "query-all" =>
            [
                Query(
                    $"CLOS? (@{Address(0)},{Address(1)},{Address(2)})",
                    "relay-query")
            ],
            "set-scan" => BuildScan(card, parameters),
            "set-trigger-source" =>
                [Write($"TRIG:SOUR {TriggerSource(parameters)}", "scan-control")],
            "query-trigger-source" => [Query("TRIG:SOUR?", "scan-query")],
            "set-arm-count" =>
                [Write($"ARM:COUN {Args.Int(parameters, "count", 1, 32767)}",
                    "scan-control")],
            "query-arm-count" => [Query("ARM:COUN?", "scan-query")],
            "initiate-scan" => [Write("INIT", "scan-control")],
            "abort-scan" => [Write("ABOR", "scan-control")],
            "bus-trigger" => [Write("*TRG", "scan-control")],
            "query-scan-complete" =>
                [Query("STAT:OPER:EVEN?", "scan-query")],
            _ => throw new ArgumentException($"Unknown operation: {operation}")
        };
    }

    private static IReadOnlyList<GeneratedCommand> BuildScan(
        int card,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        int first = Args.Int(parameters, "firstSwitch", 0, 2);
        int last = Args.Int(parameters, "lastSwitch", 0, 2);

        if (last < first)
            throw new ArgumentException(
                "lastSwitch must be greater than or equal to firstSwitch");

        return
        [
            new(
                $"SCAN (@{card:D2}{first:D2}:{card:D2}{last:D2})",
                false,
                "scan-control")
        ];
    }

    private static string TriggerSource(
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (!parameters.TryGetValue("source", out JsonElement value))
            throw new ArgumentException("source is required");

        string source = (value.GetString() ?? "").Trim().ToUpperInvariant();

        return source switch
        {
            "BUS" => "BUS",
            "EXT" or "EXTERNAL" => "EXT",
            "HOLD" => "HOLD",
            "IMM" or "IMMEDIATE" => "IMM",
            _ => throw new ArgumentException(
                "source must be BUS, EXT, HOLD, or IMM")
        };
    }
}
