#!/usr/bin/env bash
# Native-only release packaging. Requires an existing trusted native bootstrap;
# it never builds the C# oracle. Packages become visible in the output feed only
# after fixed-point verification and a build/run using the packaged SDK.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${CALADESI_PACK_OUT:-$root/artifacts/packages}"
work="${CALADESI_PACK_WORK:-$root/artifacts/pack-obj}"
version="${CALADESI_VERSION:-}"
config="${CONFIG:-Release}"
bootstrap="${CALADESI_BOOTSTRAP_DIR:?Set CALADESI_BOOTSTRAP_DIR to a trusted native bootstrap directory; C# fallback is forbidden.}"
bootstrap_identity="${CALADESI_BOOTSTRAP_RELEASE_IDENTITY:-false}"

mkdir -p "$out" "$work"
out="$(cd "$out" && pwd)"
work="$(cd "$work" && pwd)"
bootstrap="$(cd "$bootstrap" && pwd)"
if [[ "$work" == "/" || "$work" == "$root" || "$root" == "$work/"* || "$bootstrap" == "$work" || "$bootstrap" == "$work/"* || "$out" == "$work" || "$out" == "$work/"* ]]; then
    echo "Packaging work must be separate from the repository root, trusted bootstrap and output feed." >&2
    exit 1
fi

if [[ -z "$version" ]]; then
    version="$(dotnet msbuild "$root/src/Caladesi.Templates/Caladesi.Templates.csproj" -getProperty:Version --nologo)"
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]]; then
    echo "Invalid package version: $version" >&2
    exit 1
fi
rm -rf "$work"
mkdir -p "$work/packages"

echo "== Native release fixed-point verification"
dotnet msbuild "$root/eng/SelfHost.proj" -t:Verify \
    -p:BootstrapDirectory="$bootstrap" -p:SelfHostArtifactsDirectory="$work/selfhost" \
    -p:BootstrapReleaseIdentity="$bootstrap_identity" \
    -p:SelfHostReleaseIdentity=true -p:Version="$version" -p:Configuration="$config" \
    -p:ContinuousIntegrationBuild=true --nologo -v:minimal

stage="$work/selfhost/stage3"
pivot="$(printf '%s' "$config" | tr '[:upper:]' '[:lower:]')"
cli="$stage/bin/Caladesi.Cli.SelfHost/$pivot"
tasks="$stage/bin/Caladesi.Build.Tasks.SelfHost/$pivot"
runtime="$stage/bin/Caladesi.Runtime.SelfHost/$pivot"
proof="$work/selfhost/verified.txt"
common=(-c "$config" --artifacts-path "$work/pack" -p:Version="$version"
    -p:ContinuousIntegrationBuild=true -p:BuildProjectReferences=false -p:NoBuild=true -p:SelfHostReleaseIdentity=true
    -p:NativeVerificationManifest="$proof" -p:NativeReleaseRoot="$stage/"
    -p:CalacPublishDir="$cli/" -p:CaladesiTasksDir="$tasks/" -p:NativeRuntimeDir="$runtime/"
    --nologo -v:q)

echo "== Native Caladesi.Runtime"
dotnet pack "$root/eng/Caladesi.Runtime.Package.csproj" "${common[@]}" -o "$work/packages"

echo "== Native Caladesi.Sdk"
dotnet pack "$root/src/Caladesi.Sdk/Caladesi.Sdk.csproj" "${common[@]}" -o "$work/packages"

echo "== Caladesi.Templates"
dotnet pack "$root/src/Caladesi.Templates/Caladesi.Templates.csproj" "${common[@]}" -o "$work/packages"

echo "== Packaged SDK build/run proof"
mkdir -p "$work/smoke"
cat > "$work/smoke/NuGet.Config" <<EOF
<configuration>
  <packageSources><clear /><add key="native-release" value="$work/packages" /></packageSources>
</configuration>
EOF
# A private cache prevents SDK resolution from reusing an installed C# package
# with the same version. Both the SDK and its runtime must come from this feed.
export NUGET_PACKAGES="$work/package-cache"
dotnet new install "$work/packages/Caladesi.Templates.$version.nupkg" \
    --debug:custom-hive "$work/template-hive"
dotnet new calaconsole --name Smoke --output "$work/smoke" \
    --debug:custom-hive "$work/template-hive"
dotnet build "$work/smoke/Smoke.calaproj" -c "$config" --nologo -v:minimal \
    -p:PublishAot=false -p:UseAppHost=false
output="$(dotnet "$work/smoke/bin/$config/net10.0/Smoke.dll")"
if [[ "$output" != "Hello, World!" ]]; then
    echo "Packaged console template did not produce the expected output: $output" >&2
    exit 1
fi

echo "== Publishing verified packages"
for package in Caladesi.Runtime Caladesi.Sdk Caladesi.Templates; do
    cp "$work/packages/$package.$version.nupkg" "$out/"
done
echo "Packed to $out:"
printf '%s\n' "$out"/*.nupkg
