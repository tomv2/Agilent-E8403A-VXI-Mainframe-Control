#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; OUT="$ROOT/release"; rm -rf "$OUT"; mkdir -p "$OUT/bin" "$OUT/drivers" "$OUT/config"
dotnet publish "$ROOT/src/Vxi.Broker/Vxi.Broker.csproj" -c Release -r linux-arm64 --self-contained false -o "$OUT/bin/broker"
dotnet publish "$ROOT/src/Vxi.Cli/Vxi.Cli.csproj" -c Release -r linux-arm64 --self-contained false -o "$OUT/bin/cli"
for pair in "HpE1472A:hp-e1472a" "HpE1368A:hp-e1368a" "Racal3271:racal-3271"; do IFS=: read proj dir <<<"$pair"; mkdir -p "$OUT/drivers/$dir"; dotnet publish "$ROOT/drivers/$proj/Vxi.Driver.$proj.csproj" -c Release -r linux-arm64 --self-contained false -o "$OUT/drivers/$dir"; cp "$ROOT/drivers/$proj/driver.json" "$OUT/drivers/$dir/"; done
cp "$ROOT/config/appsettings.Development.json" "$OUT/config/appsettings.json"
python3 "$ROOT/scripts/update-hashes.py" "$OUT"
echo "Built $OUT"
