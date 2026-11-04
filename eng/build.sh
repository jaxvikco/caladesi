#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "${1:-}" == "--self-host" ]]; then
  shift
  exec dotnet msbuild eng/SelfHost.proj -t:Verify -p:Configuration="${CONFIG:-Release}" "$@"
fi
dotnet build Caladesi.slnx -c "${CONFIG:-Debug}" "$@"
