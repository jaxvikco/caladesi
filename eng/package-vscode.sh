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
bootstrap="${CALADESI_BOOTSTRAP_DIR:?Set CALADESI_BOOTSTRAP_DIR to a trusted native bootstrap directory; C# fallback is forbidden.}"
bootstrap_identity="${CALADESI_BOOTSTRAP_RELEASE_IDENTITY:-false}"
work="$root/artifacts/vscode-selfhost"

mkdir -p "$out"

echo "== cala-ls $version (framework-dependent)"
dotnet msbuild "$root/eng/SelfHost.proj" -t:Verify \
  -p:BootstrapDirectory="$bootstrap" -p:BootstrapReleaseIdentity="$bootstrap_identity" \
  -p:SelfHostArtifactsDirectory="$work" -p:SelfHostReleaseIdentity=true \
  -p:Version="$version" -p:Configuration=Release -p:ContinuousIntegrationBuild=true --nologo -v:minimal
stage="$work/stage3"
dotnet msbuild "$root/eng/NativePackaging.targets" -t:_RequireVerifiedNativeRelease \
  -p:NativeVerificationManifest="$work/verified.txt" -p:NativeReleaseRoot="$stage/" \
  -p:NativeRuntimeDir="$stage/bin/Caladesi.Runtime.SelfHost/release/" \
  -p:CalacPublishDir="$stage/bin/Caladesi.Cli.SelfHost/release/" \
  -p:CaladesiTasksDir="$stage/bin/Caladesi.Build.Tasks.SelfHost/release/" \
  -p:Version="$version" -p:Configuration=Release --nologo -v:minimal
rm -rf "$ext/server"
mkdir -p "$ext/server"
cp "$stage/bin/Caladesi.LanguageServer.SelfHost/release/"* "$ext/server/"
node "$root/eng/verify-language-server.mjs" "$ext/server/cala-ls.dll"

echo "== VSIX"
cd "$ext"
npm ci --no-audit --no-fund
npx vsce package --no-dependencies --out "$out/caladesi.vsix"
