# Raspberry Pi 5 first-test checklist

1. Use Raspberry Pi OS Lite 64-bit and update it.
2. Install .NET 8 SDK and confirm `dotnet --info` reports `linux-arm64`.
3. Install/configure linux-gpib for the exact adapter.
4. Confirm `/dev/gpib0` exists and test the E1406A with linux-gpib tools first.
5. Run `./scripts/build-rpi.sh`.
6. Run `sudo ./scripts/install-rpi.sh ./release`.
7. Reconnect SSH, edit `/etc/vxi-controller/appsettings.json`, and confirm all GPIB/card addresses.
8. Start the service and inspect `journalctl -u vxi-broker -f`.
9. Run `vxi status`, `vxi drivers`, `vxi devices`, and `vxi describe <instrument>`.
10. Run every intended operation without `--live` and inspect generated commands.
11. Test live with RF power removed and one relay operation at a time.
12. Keep hardware interlocks independent of the Pi for any damaging RF states.
