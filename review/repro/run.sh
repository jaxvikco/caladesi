#!/usr/bin/env bash
# Compiles one reproducer with the locally built calac (dotnet build Caladesi.slnx first) and runs it.
# usage: review/repro/run.sh review/repro/06-span-indexer-ref-return.cala
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
refdir="$(ls -d "$dotnet_root"/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 | sort | tail -1)"
out="$(mktemp -d)"
refs=(); for f in "$refdir"/*.dll; do refs+=("-ref:$f"); done
runtime="$root/src/Caladesi.Runtime/bin/Debug/net10.0/Caladesi.Runtime.dll"
dotnet "$root/src/Caladesi.Cli/bin/Debug/net10.0/calac.dll" -out:"$out/app.dll" -pdb:"$out/app.pdb" -assemblyname:app -rootnamespace:App -projectdir:"$(pwd)" "${refs[@]}" -ref:"$runtime" "$@"
rc=$?; echo "[calac exit $rc]"; [ $rc -ne 0 ] && exit $rc
cp "$runtime" "$out/"
echo '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' > "$out/app.runtimeconfig.json"
dotnet "$out/app.dll"; echo "[app exit $?]"
