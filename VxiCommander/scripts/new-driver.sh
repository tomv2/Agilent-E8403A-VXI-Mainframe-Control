#!/usr/bin/env bash
set -euo pipefail
[[ $# -eq 2 ]] || { echo "Usage: $0 Vendor Model"; exit 2; }; V="$1"; M="$2"; D="drivers/${V}${M}"; mkdir -p "$D"; cp drivers/Template/* "$D/"; sed -i "s/TEMPLATE_VENDOR/$V/g;s/TEMPLATE_MODEL/$M/g;s/template.$M/${V,,}.${M,,}/g" "$D"/*; echo "Created $D"
