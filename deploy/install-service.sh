#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH="$ROOT/publish"
SERVICE="gpstcpproxy.service"
SERVICE_USER="${SERVICE_USER:-$(whoami)}"

echo "==> Publish Release..."
dotnet publish "$ROOT/GpsTcpProxy.csproj" -c Release -o "$PUBLISH" --no-self-contained

echo "==> Copy config..."
for f in appsettings.json devices.json users.json; do
  if [[ -f "$ROOT/$f" ]]; then
    cp "$ROOT/$f" "$PUBLISH/$f"
  else
    echo "WARN: $ROOT/$f not found"
  fi
done

echo "==> Migrate database (if needed)..."
mkdir -p "$PUBLISH/data"
if [[ ! -f "$PUBLISH/data/telemetry.db" ]]; then
  for src in "$ROOT/bin/Release/net8.0/data/telemetry.db" "$ROOT/bin/Debug/net8.0/data/telemetry.db"; do
    if [[ -f "$src" ]]; then
      cp "$src" "$PUBLISH/data/telemetry.db"
      echo "Copied DB from $src"
      break
    fi
  done
fi

echo "==> Stop manual instances..."
pkill -x GpsTcpProxy 2>/dev/null || true
sleep 1

echo "==> Install systemd unit (user: $SERVICE_USER)..."
sed \
  -e "s|__SERVICE_USER__|$SERVICE_USER|g" \
  -e "s|__INSTALL_ROOT__|$ROOT|g" \
  "$ROOT/deploy/gpstcpproxy.service.template" | sudo tee "/etc/systemd/system/$SERVICE" > /dev/null

sudo systemctl daemon-reload
sudo systemctl enable "$SERVICE"
sudo systemctl restart "$SERVICE"

echo "==> Status:"
sudo systemctl status "$SERVICE" --no-pager -l
