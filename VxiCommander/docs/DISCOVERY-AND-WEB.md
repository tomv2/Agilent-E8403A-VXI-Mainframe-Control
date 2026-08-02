# Discovery and web inventory

The configuration no longer contains separate connection entries for the Racal or HP modules.

`appsettings.json` now defines only physical buses, installed drivers, security and web settings. Confirmed instruments are stored in `/var/lib/vxi-controller/inventory.json`.

After upgrading an existing installation:

```bash
./scripts/build-rpi.sh
sudo ./scripts/install-rpi.sh ./release
sudo systemctl restart vxi-broker
```

Open the web UI through an SSH tunnel and recreate/confirm the inventory. Existing `/etc/vxi-controller/appsettings.json` files using `connections` and `instruments` must be replaced by the new generated configuration.
