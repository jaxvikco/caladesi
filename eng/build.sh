#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build Caladesi.slnx -c "${CONFIG:-Debug}" "$@"
