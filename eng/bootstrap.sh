#!/usr/bin/env bash
# Explicit transition trust root: compile native sources with the retained C#
# oracle, test them, then expose a complete native seed. Never called by pack.sh.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
config="${CONFIG:-Release}"
work="$root/artifacts/bootstrap-build"
seed="$root/artifacts/bootstrap"
pivot="$(printf '%s' "$config" | tr '[:upper:]' '[:lower:]')"

rm -rf "$work" "$seed"
dotnet build "$root/Caladesi.slnx" -c "$config" --artifacts-path "$work" \
    -p:PublishAot=false --nologo
for project in \
    tests/Caladesi.Compiler.Tests/Caladesi.Compiler.Tests.csproj \
    tests/Caladesi.Compiler.SelfHost.Tests/Caladesi.Compiler.SelfHost.Tests.calaproj; do
    dotnet test --project "$root/$project" -c "$config" --artifacts-path "$work" --no-build
done

mkdir -p "$seed"
cp "$work/bin/Caladesi.Cli.SelfHost/$pivot/"* "$seed/"
cp "$work/bin/Caladesi.Build.Tasks.SelfHost/$pivot/Caladesi.Build.Tasks.SelfHost.dll" "$seed/"
dotnet "$seed/calac-selfhost.dll" --version
{
    echo "TrustRoot=retained-csharp-oracle"
    echo "SourceRevision=$(git -C "$root" rev-parse HEAD)"
    echo "DotnetSdk=$(dotnet --version)"
    echo "Configuration=$config"
    if [[ -z "$(git -C "$root" status --porcelain)" ]]; then
        echo "WorkingTree=clean"
    else
        echo "WorkingTree=dirty"
    fi
    (cd "$seed" && shasum -a 256 ./*.dll ./*.json)
} > "$seed/provenance.txt"
echo "Tested native bootstrap: $seed"
