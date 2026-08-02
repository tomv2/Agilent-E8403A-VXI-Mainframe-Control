# VXI Commander

Linux-first, headless VXI/GPIB control for Raspberry Pi 5. The architecture is inspired by SoapySDR's discover-and-match model, but hardware drivers run as isolated executables rather than being loaded into the broker process.

## Model

- **Bus**: a physical Linux-GPIB adapter, normally `gpib0`.
- **Discovery**: scans GPIB primary and secondary listeners and performs read-only identification queries.
- **Inventory**: the confirmed modules/instruments, including physical slot, logical address and switchbox card number.
- **Driver**: an independent executable that describes operations and translates them into instrument commands.

The Racal 3271 is a driver-backed module/instrument endpoint, not a transport connection. HP E1472A and E1368A modules normally share the E1406A switchbox endpoint but retain separate inventory entries and switchbox card numbers.

## Build

```bash
./scripts/build-rpi.sh
sudo ./scripts/install-rpi.sh ./release
```

Reconnect SSH after installation, then:

```bash
sudo systemctl enable --now vxi-broker
systemctl status vxi-broker --no-pager
vxi status
vxi drivers
vxi discover
```

## Web inventory

The web interface binds only to `127.0.0.1:8080` on the Pi. From Windows, create an SSH tunnel:

```bash
ssh -L 8080:127.0.0.1:8080 root1@rpi5-host
```

Keep that SSH session open and browse to:

```text
http://127.0.0.1:8080
```

The page can:

- run a read-only GPIB discovery scan;
- show raw E1406A configuration responses;
- add, remove and edit module inventory entries;
- assign driver, primary/secondary address, physical slot, logical address and switchbox card number;
- save inventory to `/var/lib/vxi-controller/inventory.json`.

## CLI

```bash
vxi status
vxi drivers
vxi devices
vxi discover
vxi describe rf-mux-a
vxi operate rf-mux-a select --module 1 --channel 1
vxi operate rf-mux-a select --module 1 --channel 1 --live
```

Operations are dry-run unless `--live` is explicitly supplied.

## Discovery limitations

Discovery is intentionally read-only. It scans GPIB listeners, attempts `*IDN?`, and asks identified E1406A controllers for `VXI:CONF:DLIS?`. Older modules and switchbox configurations do not always report enough structured information to safely infer every module model, physical slot or switchbox card number. The web page therefore treats discovery as a proposal and requires a confirmed inventory before control.

## Security

- Broker owns Linux-GPIB access.
- Drivers run out of process and receive structured requests only.
- Driver executable hashes can be required in production.
- Generated commands are checked against category and prefix allow-lists.
- Unix socket is limited to the `vxi-operators` group.
- Web UI is localhost-only and intended to be reached through SSH port forwarding.
- Inventory and audit files are writable only by the broker/operator group.
