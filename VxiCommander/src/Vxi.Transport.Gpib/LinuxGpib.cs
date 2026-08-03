using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Vxi.Transport.Gpib;

public sealed record GpibConnectionConfig(
    int BoardIndex,
    int PrimaryAddress,
    int SecondaryAddress,
    int TimeoutCode = 13,
    bool SendEoi = true,
    int EosMode = 0);

public sealed class LinuxGpib : IDisposable
{
    [DllImport("libgpib.so.0")] private static extern int ibdev(int board, int pad, int sad, int timeout, int eot, int eos);
    [DllImport("libgpib.so.0")] private static extern int ibwrt(int descriptor, byte[] data, nuint count);
    [DllImport("libgpib.so.0")] private static extern int ibrd(int descriptor, byte[] data, nuint count);
    [DllImport("libgpib.so.0")] private static extern int ibclr(int descriptor);
    [DllImport("libgpib.so.0")] private static extern int ibonl(int descriptor, int online);
    [DllImport("libgpib.so.0")] private static extern int ThreadIbsta();
    [DllImport("libgpib.so.0")] private static extern int ThreadIberr();
    [DllImport("libgpib.so.0")] private static extern long ThreadIbcntl();

    private const int ErrorFlag = 0x8000;
    private readonly int _descriptor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LinuxGpib(GpibConnectionConfig config)
    {
        ValidateAddress(config.PrimaryAddress, nameof(config.PrimaryAddress));
        ValidateAddress(config.SecondaryAddress, nameof(config.SecondaryAddress));

        // linux-gpib uses 0 to mean "no secondary addressing". A real SAD N is
        // represented by the IEEE-488 listen/talk address byte 0x60 + N.
        int encodedSecondaryAddress = EncodeSecondaryAddress(config.SecondaryAddress);
        _descriptor = ibdev(
            config.BoardIndex,
            config.PrimaryAddress,
            encodedSecondaryAddress,
            config.TimeoutCode,
            config.SendEoi ? 1 : 0,
            config.EosMode);
        Check("ibdev");
    }

    public static int EncodeSecondaryAddress(int secondaryAddress)
    {
        ValidateAddress(secondaryAddress, nameof(secondaryAddress));
        return 0x60 + secondaryAddress;
    }

    public async Task<string?> ExecuteAsync(string command, bool query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(command.TrimEnd('\r', '\n') + "\n");
            int status = ibwrt(_descriptor, bytes, (nuint)bytes.Length);
            Check("ibwrt", status);

            if (!query) return null;

            byte[] buffer = new byte[65536];
            status = ibrd(_descriptor, buffer, (nuint)(buffer.Length - 1));
            Check("ibrd", status);
            int count = checked((int)Math.Clamp(ThreadIbcntl(), 0, buffer.Length - 1));
            return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\0', '\r', '\n');
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            int status = ibclr(_descriptor);
            Check("ibclr", status);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateAddress(int value, string name)
    {
        if (value is < 0 or > 30) throw new ArgumentOutOfRangeException(name, "GPIB addresses must be 0..30.");
    }

    private static void Check(string operation, int? statusOverride = null)
    {
        int status = statusOverride ?? ThreadIbsta();
        if ((status & ErrorFlag) != 0)
        {
            throw new IOException($"{operation} failed: ibsta=0x{status:X}, iberr={ThreadIberr()}, ibcnt={ThreadIbcntl()}");
        }
    }

    public void Dispose()
    {
        ibonl(_descriptor, 0);
        _gate.Dispose();
    }
}

public static class GpibDiscovery
{
    private static readonly Regex SwitchSadRegex = new(
        @"SWITCH\s+INSTALLED\s+AT\s+SECONDARY\s+ADDR\s+(?<sad>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<IReadOnlyList<GpibMainframeResult>> ScanAsync(
        int boardIndex,
        int timeoutCode,
        CancellationToken cancellationToken)
    {
        var found = new List<GpibMainframeResult>();
        for (int primary = 0; primary <= 30; primary++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var system = new LinuxGpib(new(boardIndex, primary, 0, timeoutCode));
                string? identification = await system.ExecuteAsync("*IDN?", true, cancellationToken);
                if (identification is null || !identification.Contains("E1406", StringComparison.OrdinalIgnoreCase)) continue;

                string rawConfiguration = await system.ExecuteAsync("VXI:CONF:DLIS?", true, cancellationToken) ?? string.Empty;
                IReadOnlyList<VxiDeviceRecord> devices = DlisParser.Parse(rawConfiguration);
                int? switchSecondaryAddress = ParseSwitchSecondaryAddress(rawConfiguration);
                string? switchIdentification = null;
                string? switchError = null;

                if (switchSecondaryAddress is int sad)
                {
                    try
                    {
                        using var switchbox = new LinuxGpib(new(boardIndex, primary, sad, timeoutCode));
                        switchIdentification = await switchbox.ExecuteAsync("*IDN?", true, cancellationToken);
                        switchError = await switchbox.ExecuteAsync("SYST:ERR?", true, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        switchError = ex.Message;
                    }
                }

                found.Add(new(
                    boardIndex,
                    primary,
                    identification,
                    switchSecondaryAddress,
                    switchIdentification,
                    switchError,
                    rawConfiguration,
                    devices));
            }
            catch
            {
                // A failed open/query means this PAD/SAD0 is not an E1406A endpoint.
            }
        }

        return found;
    }

    private static int? ParseSwitchSecondaryAddress(string rawConfiguration)
    {
        Match match = SwitchSadRegex.Match(rawConfiguration);
        return match.Success && int.TryParse(match.Groups["sad"].Value, out int sad) ? sad : null;
    }
}

public static class DlisParser
{
    public static IReadOnlyList<VxiDeviceRecord> Parse(string response)
    {
        var result = new List<VxiDeviceRecord>();
        foreach (string rawRecord in SplitRecords(response))
        {
            IReadOnlyList<string> fields = SplitCsv(rawRecord);
            if (fields.Count < 14) continue;

            result.Add(new VxiDeviceRecord(
                ParseInt(fields[0]),
                ParseInt(fields[1]),
                ParseInt(fields[2]),
                ParseInt(fields[3]),
                ParseNullableInt(fields[4]),
                ParseInt(fields[5]),
                fields[6],
                fields[7],
                fields[8],
                fields[9],
                fields[10],
                Unquote(fields.ElementAtOrDefault(14) ?? fields[^1])));
        }
        return result;
    }

    private static IEnumerable<string> SplitRecords(string value)
    {
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"') quoted = !quoted;
            if (c == ';' && !quoted)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static IReadOnlyList<string> SplitCsv(string value)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"') quoted = !quoted;
            if (c == ',' && !quoted)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(c);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static int ParseInt(string value) => int.TryParse(value.Trim().TrimStart('+'), out int number) ? number : 0;
    private static int? ParseNullableInt(string value) => int.TryParse(value.Trim().TrimStart('+'), out int number) && number >= 0 ? number : null;
    private static string Unquote(string value) => value.Trim().Trim('"');
}

public sealed record GpibMainframeResult(
    int BoardIndex,
    int PrimaryAddress,
    string Identification,
    int? SwitchSecondaryAddress,
    string? SwitchIdentification,
    string? SwitchError,
    string RawConfiguration,
    IReadOnlyList<VxiDeviceRecord> Devices);

public sealed record VxiDeviceRecord(
    int LogicalAddress,
    int CommanderLogicalAddress,
    int ManufacturerId,
    int DeviceType,
    int? PhysicalSlot,
    int Mainframe,
    string DeviceClass,
    string AddressSpace,
    string Offset,
    string Size,
    string Status,
    string Description);
