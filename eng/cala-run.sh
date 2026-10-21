#!/usr/bin/env bash
# Compiles one or more .cala files with the locally built calac and runs the result (development helper).
# usage: eng/cala-run.sh file.cala [more.cala ...]   (set CALADESI_OUT to keep the output directory)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
refdir="$(ls -d /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 | sort | tail -1)"
out="${CALADESI_OUT:-$(mktemp -d)}"
mkdir -p "$out"
refs=()
for f in "$refdir"/*.dll; do refs+=("-ref:$f"); done
runtime="$root/src/Caladesi.Runtime/bin/Debug/net10.0/Caladesi.Runtime.dll"
dotnet "$root/src/Caladesi.Cli/bin/Debug/net10.0/calac.dll" -out:"$out/app.dll" -pdb:"$out/app.pdb" -assemblyname:app -rootnamespace:App \
  -projectdir:"$(pwd)" "${refs[@]}" -ref:"$runtime" "$@"
cp "$runtime" "$out/"
echo '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' > "$out/app.runtimeconfig.json"
dotnet "$out/app.dll"
