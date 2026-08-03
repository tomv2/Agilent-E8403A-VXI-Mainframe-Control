#!/usr/bin/env bash
set -e

if systemctl list-unit-files vxi-broker.service >/dev/null 2>&1; then
    systemctl stop vxi-broker.service || true
fi
uo pipefail
[[ $EUID -eq 0 ]] || { echo "Run with sudo"; exit 1; }
REL="${1:-./release}"; OPERATOR="${SUDO_USER:-pi}"
getent group vxi-operators >/dev/null || groupadd --system vxi-operators
id vxi-broker >/dev/null 2>&1 || useradd --system --home /var/lib/vxi-controller --shell /usr/sbin/nologin vxi-broker
usermod -aG vxi-operators "$OPERATOR"
install -d -o root -g root -m 0755 /opt/vxi-controller /usr/lib/vxi-controller/drivers
install -d -o root -g vxi-broker -m 0750 /etc/vxi-controller
install -d -o vxi-broker -g vxi-operators -m 0770 /var/lib/vxi-controller /var/log/vxi-controller
cp -a "$REL/bin" /opt/vxi-controller/
cp -a "$REL/drivers/." /usr/lib/vxi-controller/drivers/
install -m 0640 -o root -g vxi-broker "$REL/config/appsettings.json" /etc/vxi-controller/appsettings.json
[[ -f /var/lib/vxi-controller/inventory.json ]] || echo '[]' > /var/lib/vxi-controller/inventory.json
chown vxi-broker:vxi-operators /var/lib/vxi-controller/inventory.json
chmod 0660 /var/lib/vxi-controller/inventory.json
ln -sf /opt/vxi-controller/bin/cli/vxi /usr/local/bin/vxi
install -D -m 0644 "$(dirname "$0")/../packaging/systemd/vxi-broker.service" /etc/systemd/system/vxi-broker.service
install -D -m 0644 "$(dirname "$0")/../packaging/udev/99-vxi-gpib.rules" /etc/udev/rules.d/99-vxi-gpib.rules
chown -R root:root /usr/lib/vxi-controller/drivers
find /usr/lib/vxi-controller/drivers -type d -exec chmod 0755 {} +
find /usr/lib/vxi-controller/drivers -type f -exec chmod 0555 {} +
systemctl daemon-reload
udevadm control --reload-rules
echo "Installed. Reconnect SSH, then: sudo systemctl enable --now vxi-broker"
echo "Web UI: ssh -L 8080:127.0.0.1:8080 $OPERATOR@<pi-address>"

SERVICE_USER=$(systemctl show vxi-broker -p User --value)
SERVICE_GROUP=$(systemctl show vxi-broker -p Group --value)

chown root:"$SERVICE_GROUP" /etc/vxi-controller
chmod 0750 /etc/vxi-controller

chown root:"$SERVICE_GROUP" /etc/vxi-controller/appsettings.json
chmod 0640 /etc/vxi-controller/appsettings.json

install -d \
  -o "$SERVICE_USER" \
  -g "$SERVICE_GROUP" \
  -m 0750 \
  /var/lib/vxi-controller \
  /var/log/vxi-controller
