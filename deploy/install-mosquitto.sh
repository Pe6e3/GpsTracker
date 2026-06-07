#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MQTT_USER="${MQTT_USER:-gpstcpproxy}"
MQTT_PASSWORD="${MQTT_PASSWORD:-gpstcpproxy}"
GENERATE_TLS="${GENERATE_TLS:-1}"

echo "==> Install Mosquitto..."
sudo apt-get update
sudo apt-get install -y mosquitto mosquitto-clients openssl

echo "==> Configure Mosquitto..."
sudo mkdir -p /etc/mosquitto/certs
sudo cp "$ROOT/deploy/mosquitto.conf" /etc/mosquitto/conf.d/gpstcpproxy.conf

echo "==> Create MQTT user $MQTT_USER..."
if ! sudo mosquitto_passwd -b /etc/mosquitto/passwd "$MQTT_USER" "$MQTT_PASSWORD" 2>/dev/null; then
  sudo mosquitto_passwd -c -b /etc/mosquitto/passwd "$MQTT_USER" "$MQTT_PASSWORD"
else
  sudo mosquitto_passwd -b /etc/mosquitto/passwd "$MQTT_USER" "$MQTT_PASSWORD"
fi

if [[ "$GENERATE_TLS" == "1" ]]; then
  echo "==> Generate self-signed TLS certificate..."
  if [[ ! -f /etc/mosquitto/certs/server.crt ]]; then
    sudo openssl req -new -x509 -days 825 -nodes \
      -subj "/CN=$(hostname -f 2>/dev/null || hostname)" \
      -out /etc/mosquitto/certs/server.crt \
      -keyout /etc/mosquitto/certs/server.key
    sudo cp /etc/mosquitto/certs/server.crt /etc/mosquitto/certs/ca.crt
    sudo chmod 640 /etc/mosquitto/certs/server.key
    sudo chown root:mosquitto /etc/mosquitto/certs/server.key
  fi
fi

echo "==> Enable and restart Mosquitto..."
sudo systemctl enable mosquitto
sudo systemctl restart mosquitto

echo "==> Open firewall ports (if ufw active)..."
if command -v ufw >/dev/null 2>&1 && sudo ufw status | grep -q "Status: active"; then
  sudo ufw allow 1883/tcp comment 'MQTT'
  sudo ufw allow 8883/tcp comment 'MQTT TLS'
fi

echo "==> Status:"
sudo systemctl status mosquitto --no-pager -l | head -15

echo
echo "MQTT user: $MQTT_USER"
echo "Update appsettings.json Mqtt.Username / Mqtt.Password to match."
echo "Test publish:"
echo "  mosquitto_pub -h localhost -u $MQTT_USER -P '$MQTT_PASSWORD' -t owntracks/test/phone -m '{\"_type\":\"location\",\"lat\":43.23,\"lon\":76.86,\"tst\":'$(date +%s)'}'"
