#!/usr/bin/env bash
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run with sudo"; exit 1; }; REL="${1:-./release}"; OPERATOR="${SUDO_USER:-pi}"
getent group vxi-operators >/dev/null || groupadd --system vxi-operators
id vxi-broker >/dev/null 2>&1 || useradd --system --home /var/lib/vxi-controller --shell /usr/sbin/nologin vxi-broker
usermod -aG vxi-operators "$OPERATOR"
install -d -o root -g root -m 0755 /opt/vxi-controller /usr/lib/vxi-controller/drivers /etc/vxi-controller
cp -a "$REL/bin" /opt/vxi-controller/; cp -a "$REL/drivers/." /usr/lib/vxi-controller/drivers/; install -m 0640 -o root -g vxi-broker "$REL/config/appsettings.json" /etc/vxi-controller/appsettings.json
ln -sf /opt/vxi-controller/bin/cli/vxi /usr/local/bin/vxi
install -D -m 0644 "$(dirname "$0")/../packaging/systemd/vxi-broker.service" /etc/systemd/system/vxi-broker.service
install -D -m 0644 "$(dirname "$0")/../packaging/udev/99-vxi-gpib.rules" /etc/udev/rules.d/99-vxi-gpib.rules
chown -R root:root /usr/lib/vxi-controller/drivers; find /usr/lib/vxi-controller/drivers -type d -exec chmod 0755 {} +; find /usr/lib/vxi-controller/drivers -type f -exec chmod 0555 {} +
systemctl daemon-reload; udevadm control --reload-rules; echo "Installed. Reconnect SSH, then: sudo systemctl enable --now vxi-broker"
