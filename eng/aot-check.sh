#!/usr/bin/env bash
# Publishes each AOT-enforced project with PublishAot into a temp dir, fails on any
# IL2xxx/IL3xxx warning (or any build error), then deletes the output. The native
# binaries exist only to enforce AOT/trim compatibility and are discarded.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${AOT_RID:-linux-x64}"
projects=(
  "src/Caladesi.Compiler/Caladesi.Compiler.csproj"
  "src/Caladesi.Runtime/Caladesi.Runtime.csproj"
  "src/Caladesi.Cli/Caladesi.Cli.csproj"
  "src/Caladesi.LanguageServer/Caladesi.LanguageServer.csproj"
)
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
status=0
for p in "${projects[@]}"; do
  name="$(basename "$p" .csproj)"
  out="$tmp/$name"
  log="$tmp/$name.log"
  echo "== AOT publish: $name"
  case "$name" in
    Caladesi.Compiler|Caladesi.Runtime)
      # Libraries are verified through the analyzers (IsAotCompatible) and by being
      # published as part of the executables that reference them.
      dotnet build "$root/$p" -c Release -o "$out" -p:EnableAotAnalyzer=true -p:TreatWarningsAsErrors=true >"$log" 2>&1 || { cat "$log"; status=1; continue; }
      ;;
    *)
      dotnet publish "$root/$p" -c Release -r "$rid" -o "$out" -p:TreatWarningsAsErrors=true >"$log" 2>&1 || { cat "$log"; status=1; continue; }
      ;;
  esac
  if grep -E "warning (IL2|IL3)[0-9]{3}" "$log" >/dev/null; then
    echo "AOT/trim warnings in $name:"; grep -E "warning (IL2|IL3)[0-9]{3}" "$log" | sort -u; status=1
  fi
done
if [ $status -eq 0 ]; then echo "AOT check passed"; fi
exit $status
