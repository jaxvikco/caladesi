#!/usr/bin/env bash
# Builds and packs everything needed to use Caladesi from a normal `dotnet` workflow into a local NuGet feed:
#   Caladesi.Runtime, Caladesi.Sdk (with calac + the MSBuild tasks inside), Caladesi.Templates.
#
#   eng/pack.sh                       -> artifacts/packages (version 1.0.0)
#   CALADESI_PACK_OUT=/some/dir eng/pack.sh
#
# Intermediate output goes to artifacts/pack-obj (via --artifacts-path, so the normal bin/obj folders are not
# touched). Restore may need the NuGet proxy for packages that are not yet in the global packages cache.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${CALADESI_PACK_OUT:-$root/artifacts/packages}"
work="${CALADESI_PACK_WORK:-$root/artifacts/pack-obj}"
version="${CALADESI_VERSION:-1.0.0}"
config="${CONFIG:-Release}"

rm -rf "$work"
mkdir -p "$out" "$work"
rm -f "$out"/Caladesi.Runtime.*.nupkg "$out"/Caladesi.Sdk.*.nupkg "$out"/Caladesi.Templates.*.nupkg

common=(-c "$config" --artifacts-path "$work/build" -p:Version="$version" -p:ContinuousIntegrationBuild=true --nologo -v:q)

echo "== calac (framework-dependent publish)"
dotnet publish "$root/src/Caladesi.Cli/Caladesi.Cli.csproj" "${common[@]}" -p:PublishAot=false -p:UseAppHost=false -o "$work/calac"

echo "== Caladesi.Build.Tasks"
dotnet build "$root/src/Caladesi.Build.Tasks/Caladesi.Build.Tasks.csproj" "${common[@]}" -o "$work/tasks"

echo "== Caladesi.Runtime"
dotnet pack "$root/src/Caladesi.Runtime/Caladesi.Runtime.csproj" "${common[@]}" -o "$out"

echo "== Caladesi.Sdk"
dotnet pack "$root/src/Caladesi.Sdk/Caladesi.Sdk.csproj" "${common[@]}" -p:CalacPublishDir="$work/calac/" -p:CaladesiTasksDir="$work/tasks/" -o "$out"

echo "== Caladesi.Templates"
dotnet pack "$root/src/Caladesi.Templates/Caladesi.Templates.csproj" "${common[@]}" -o "$out"

echo "Packed to $out:"
ls -1 "$out"
