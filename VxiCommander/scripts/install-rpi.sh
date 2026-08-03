#!/usr/bin/env bash
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run with sudo"; exit 1; }
REL="${1:-./release}"
OPERATOR="${SUDO_USER:-pi}"

getent group vxi-operators >/dev/null || groupadd --system vxi-operators
id vxi-broker >/dev/null 2>&1 || useradd --system --home /var/lib/vxi-controller --shell /usr/sbin/nologin vxi-broker
usermod -aG vxi-operators "$OPERATOR"

install -d -o root -g root -m 0755 /opt/vxi-controller /usr/lib/vxi-controller/drivers
install -d -o root -g vxi-broker -m 0750 /etc/vxi-controller
install -d -o vxi-broker -g vxi-operators -m 0770 /var/lib/vxi-controller /var/log/vxi-controller

rm -rf /opt/vxi-controller/bin /usr/lib/vxi-controller/drivers/*
cp -a "$REL/bin" /opt/vxi-controller/
cp -a "$REL/drivers/." /usr/lib/vxi-controller/drivers/
install -m 0640 -o root -g vxi-broker "$REL/config/appsettings.json" /etc/vxi-controller/appsettings.json

[[ -f /var/lib/vxi-controller/inventory.json ]] || echo '[]' > /var/lib/vxi-controller/inventory.json
chown vxi-broker:vxi-operators /var/lib/vxi-controller/inventory.json
chmod 0660 /var/lib/vxi-controller/inventory.json

ln -sf /opt/vxi-controller/bin/cli/vxi /usr/local/bin/vxi
install -D -m 0644 "$(dirname "$0")/../packaging/systemd/vxi-gpib-init.service" /etc/systemd/system/vxi-gpib-init.service
install -D -m 0644 "$(dirname "$0")/../packaging/systemd/vxi-broker.service" /etc/systemd/system/vxi-broker.service
install -D -m 0644 "$(dirname "$0")/../packaging/udev/99-vxi-gpib.rules" /etc/udev/rules.d/99-vxi-gpib.rules

chown -R root:root /usr/lib/vxi-controller/drivers /opt/vxi-controller/bin
find /usr/lib/vxi-controller/drivers /opt/vxi-controller/bin -type d -exec chmod 0755 {} +
find /usr/lib/vxi-controller/drivers /opt/vxi-controller/bin -type f -exec chmod 0555 {} +

systemctl daemon-reload
udevadm control --reload-rules
systemctl enable vxi-gpib-init.service vxi-broker.service

echo "Installed. Reconnect SSH for group membership, then run:"
echo "  sudo systemctl restart vxi-gpib-init vxi-broker"
echo "  vxi discover"
echo "Web UI: ssh -L 8080:127.0.0.1:8080 $OPERATOR@<pi-address>"
echo "NOTE: external GPIB modules must be rebuilt after each kernel update unless DKMS is configured."
