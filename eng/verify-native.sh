#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
config="${CONFIG:-Release}"
bootstrap="${CALADESI_BOOTSTRAP_DIR:?Set CALADESI_BOOTSTRAP_DIR to the tested native bootstrap.}"
pivot="$(printf '%s' "$config" | tr '[:upper:]' '[:lower:]')"
work="$root/artifacts/selfhost/$pivot"
stage="$work/stage3"
tests="$root/tests/Caladesi.Compiler.SelfHost.Tests/Caladesi.Compiler.SelfHost.Tests.calaproj"

dotnet msbuild "$root/eng/SelfHost.proj" -t:Verify \
    -p:BootstrapDirectory="$bootstrap" -p:Configuration="$config" \
    -p:SelfHostArtifactsDirectory="$work" --nologo -v:minimal
dotnet build "$tests" -c "$config" --artifacts-path "$stage" \
    -p:BootstrapDirectory="$bootstrap" -p:BuildProjectReferences=false -p:RestoreRecursive=false \
    -p:CaladesiCompilerPath="$stage/bin/Caladesi.Cli.SelfHost/$pivot/calac-selfhost.dll" \
    -p:CaladesiTasksAssembly="$stage/bin/Caladesi.Build.Tasks.SelfHost/$pivot/Caladesi.Build.Tasks.SelfHost.dll" \
    -p:PublishAot=false --nologo
dotnet test --project "$tests" -c "$config" --artifacts-path "$stage" --no-build
