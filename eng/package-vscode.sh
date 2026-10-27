#!/usr/bin/env bash
# Packages the VS Code extension with the language server bundled as a framework-dependent cala-ls.dll in
# editors/vscode/server (run by the extension with `dotnet`, next to Caladesi.Runtime.dll).
#
#   eng/package-vscode.sh                            -> artifacts/vscode/caladesi.vsix
#   CALADESI_VSIX_OUT=/some/dir eng/package-vscode.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
ext="$root/editors/vscode"
out="${CALADESI_VSIX_OUT:-$root/artifacts/vscode}"
version="$(node -p "require('$ext/package.json').version")"

rm -rf "$ext/server"
mkdir -p "$out"

echo "== cala-ls $version (framework-dependent)"
# RollForward=Major: run on a newer .NET when .NET 10 is not installed.
dotnet publish "$root/src/Caladesi.LanguageServer/Caladesi.LanguageServer.csproj" -c Release \
  -p:PublishAot=false -p:UseAppHost=false -p:RollForward=Major \
  -p:Version="$version" -p:ContinuousIntegrationBuild=true \
  -o "$ext/server" --nologo -v:q

echo "== VSIX"
cd "$ext"
npm ci --no-audit --no-fund
npx vsce package --no-dependencies --out "$out/caladesi.vsix"
