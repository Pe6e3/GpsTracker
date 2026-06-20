#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH="$ROOT/publish"
SERVICE="gpstcpproxy.service"
SERVICE_USER="${SERVICE_USER:-$(whoami)}"
DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

ensure_dotnet_ready() {
	if ! command -v dotnet >/dev/null 2>&1; then
		echo "ERROR: dotnet не найден. Установите .NET SDK 8:"
		echo "  https://learn.microsoft.com/dotnet/core/install/linux"
		exit 1
	fi

	local shared_dir="$DOTNET_ROOT/shared"
	local ubuntu_shared="/usr/lib/dotnet/shared"

	if [[ ! -d "$shared_dir" && -d "$ubuntu_shared" ]]; then
		echo "==> Связка runtime Ubuntu -> $shared_dir ..."
		sudo ln -sfn "$ubuntu_shared" "$shared_dir"
	fi

	if ! dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.NETCore.App'; then
		echo "ERROR: не найден Microsoft.NETCore.App runtime для dotnet publish."
		echo "Установите runtime из репозитория Microsoft (не только Ubuntu SDK):"
		echo "  sudo apt install dotnet-runtime-8.0 aspnetcore-runtime-8.0"
		echo "Или пакеты с packages.microsoft.com:"
		echo "  sudo apt install dotnet-runtime-8.0=8.0.28-1 aspnetcore-runtime-8.0=8.0.28-1"
		exit 1
	fi
}

ensure_dotnet_ready

publish_release() {
	local staging
	staging="$(mktemp -d /tmp/gpstcpproxy-publish.XXXXXX)"

	# Старые вложенные publish/ в bin/ от sudo-сборок
	if [[ -d "$ROOT/bin/Release" || -d "$ROOT/bin/Debug" ]]; then
		rm -rf "$ROOT/bin/Release" "$ROOT/bin/Debug" 2>/dev/null || \
			sudo rm -rf "$ROOT/bin/Release" "$ROOT/bin/Debug"
	fi

	echo "==> Publish Release..."
	dotnet publish "$ROOT/GpsTcpProxy.csproj" -c Release -o "$staging" --no-self-contained

	mkdir -p "$PUBLISH/data" "$PUBLISH/logs"

	local keep_dir
	keep_dir="$(mktemp -d /tmp/gpstcpproxy-keep.XXXXXX)"
	for f in appsettings.json devices.json users.json; do
		[[ -f "$PUBLISH/$f" ]] && cp "$PUBLISH/$f" "$keep_dir/$f"
	done

	# Вложенная publish/publish/... от старых sudo-сборок
	if [[ -d "$PUBLISH/publish" ]]; then
		echo "==> Удаление вложенной publish/publish/..."
		sudo rm -rf "$PUBLISH/publish"
	fi

	# Очищаем артефакты прошлых сборок, сохраняем data/ и logs/
	local item name
	for item in "$PUBLISH"/*; do
		[[ -e "$item" ]] || continue
		name="$(basename "$item")"
		[[ "$name" == data || "$name" == logs ]] && continue
		rm -rf "$item" 2>/dev/null || sudo rm -rf "$item"
	done

	cp -a "$staging/." "$PUBLISH/"
	rm -rf "$staging"

	for f in appsettings.json devices.json users.json; do
		if [[ -f "$ROOT/$f" ]]; then
			cp "$ROOT/$f" "$PUBLISH/$f"
		elif [[ -f "$keep_dir/$f" ]]; then
			cp "$keep_dir/$f" "$PUBLISH/$f"
		fi
	done
	rm -rf "$keep_dir"

	chown -R "$SERVICE_USER:$SERVICE_USER" "$PUBLISH" 2>/dev/null || sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$PUBLISH"

	# На всякий случай — не должно появляться при сборке во временную папку
	[[ -d "$PUBLISH/publish" ]] && rm -rf "$PUBLISH/publish" 2>/dev/null || sudo rm -rf "$PUBLISH/publish"
}

publish_release

echo "==> Copy config..."
for f in appsettings.json devices.json users.json; do
  if [[ -f "$ROOT/$f" ]]; then
    cp "$ROOT/$f" "$PUBLISH/$f"
  elif [[ ! -f "$PUBLISH/$f" ]]; then
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
