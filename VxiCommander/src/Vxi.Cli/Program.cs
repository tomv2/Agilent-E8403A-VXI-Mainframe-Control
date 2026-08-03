using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vxi.Protocol;

const string LiveConfirmationPhrase = "SWITCH RELAY";
string socketPath = Environment.GetEnvironmentVariable("VXI_SOCKET") ?? "/run/vxi-controller/broker.sock";
if (args.Length == 0) { Help(); return 2; }

string command = args[0];
BrokerRequest request;
if (command is "status" or "drivers" or "devices" or "discover")
{
    request = new(command);
}
else if (command == "describe" && args.Length >= 2)
{
    request = new("describe", args[1]);
}
else if (command == "operate" && args.Length >= 3)
{
    var parameters = new Dictionary<string, JsonElement>();
    bool dryRun = true;
    string? confirmation = null;
    for (int index = 3; index < args.Length; index++)
    {
        if (args[index] == "--live") { dryRun = false; continue; }
        if (args[index] == "--confirm" && index + 1 < args.Length) { confirmation = args[++index]; continue; }
        if (args[index].StartsWith("--") && index + 1 < args.Length)
        {
            string name = args[index][2..];
            using JsonDocument document = JsonDocument.Parse(ParseValue(args[++index]));
            parameters[name] = document.RootElement.Clone();
        }
    }

    if (!dryRun && !string.Equals(confirmation, LiveConfirmationPhrase, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Live operation requires: --confirm \"{LiveConfirmationPhrase}\"");
        return 2;
    }
    request = new("operate", args[1], args[2], parameters, dryRun);
}
else
{
    Help();
    return 2;
}

using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
await using var stream = new NetworkStream(socket, ownsSocket: true);
using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
using var reader = new StreamReader(stream, Encoding.UTF8);
await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonDefaults.Options));
string? line = await reader.ReadLineAsync();
if (line is null) return 1;
using JsonDocument output = JsonDocument.Parse(line);
Console.WriteLine(JsonSerializer.Serialize(output.RootElement, new JsonSerializerOptions { WriteIndented = true }));
return output.RootElement.GetProperty("success").GetBoolean() ? 0 : 1;

static string ParseValue(string value)
{
    if (int.TryParse(value, out _) ||
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ||
        value is "true" or "false" or "null") return value;
    return JsonSerializer.Serialize(value);
}

static void Help() => Console.WriteLine(
    "vxi status | drivers | devices | discover | describe <id> | " +
    "operate <id> <operation> [--name value] [--live --confirm \"SWITCH RELAY\"]");
