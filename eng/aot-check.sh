#!/usr/bin/env bash
# Checks the native project closure using an already verified native compiler and
# task. No C# bootstrap is built. Logs and AOT outputs remain under artifacts.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${AOT_RID:-linux-x64}"
config="${CONFIG:-Release}"
manifest="${CALADESI_AOT_MANIFEST:-$root/artifacts/selfhost/${config,,}/verified.txt}"
fail() { echo "$*" >&2; exit 1; }
[[ "$rid" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || fail "Invalid AOT_RID: $rid"
[[ "$config" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || fail "Invalid CONFIG: $config"
[[ -f "$manifest" ]] || fail "Run eng/SelfHost.proj -t:Verify first and set CALADESI_AOT_MANIFEST to its verified.txt; C# fallback is forbidden."

manifest_value() {
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line#$'\xef\xbb\xbf'}"
    line="${line%$'\r'}"
    if [[ "$line" == "$1="* ]]; then printf '%s\n' "${line#*=}"; fi
  done < "$manifest"
}
[[ "$(manifest_value Verified)" == "true" ]] || fail "Native fixed-point verification is not positive."
[[ "$(manifest_value Configuration)" == "$config" ]] || fail "Native proof configuration does not match CONFIG."
version="$(manifest_value Version)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]] || fail "Native proof has an invalid version."
identity="$(manifest_value SelfHostReleaseIdentity)"
[[ "$identity" == "true" || "$identity" == "false" ]] || fail "Native proof has an invalid release identity."
stage_root="$(manifest_value ArtifactsRoot)"
[[ -d "$stage_root/stage3" ]] || fail "Native proof stage3 directory is missing."
stage_root="$(cd "$stage_root" && pwd)"
pivot="${config,,}"
compiler_name="Caladesi.Compiler.SelfHost"
runtime_name="Caladesi.Runtime.SelfHost"
task_name="Caladesi.Build.Tasks.SelfHost"
cli_name="calac-selfhost"
server_name="cala-ls-selfhost"
if [[ "$identity" == "true" ]]; then
  compiler_name="Caladesi.Compiler"
  runtime_name="Caladesi.Runtime"
  task_name="Caladesi.Build.Tasks"
  cli_name="calac"
  server_name="cala-ls"
fi

proof_files=()
for pair in "Runtime:$runtime_name" "Compiler:$compiler_name" "Build.Tasks:$task_name" "Cli:$cli_name" "LanguageServer:$server_name"; do
  project="${pair%%:*}"
  name="${pair#*:}"
  for extension in dll pdb; do
    proof_files+=("$stage_root/stage3/bin/Caladesi.$project.SelfHost/$pivot/$name.$extension")
  done
done
for pair in "Cli:$cli_name" "LanguageServer:$server_name"; do
  project="${pair%%:*}"
  name="${pair#*:}"
  for extension in deps.json runtimeconfig.json; do
    proof_files+=("$stage_root/stage3/bin/Caladesi.$project.SelfHost/$pivot/$name.$extension")
  done
done
[[ "$(grep -c '^SHA256|' "$manifest")" == "${#proof_files[@]}" ]] || fail "Native proof must contain all 14 fixed-point artifacts exactly once."
for file in "${proof_files[@]}"; do
  expected="$(awk -F'|' -v path="$file" '$1 == "SHA256" && $2 == path { sub(/\r$/, "", $3); print $3 }' "$manifest")"
  [[ "$expected" =~ ^[0-9A-Fa-f]{64}$ && -f "$file" ]] || fail "Native proof is missing or duplicates $file."
  actual="$(sha256sum -- "$file")"
  [[ "${actual%% *}" == "${expected,,}" ]] || fail "Native artifact changed after verification: $file"
done
verified_cli="$stage_root/stage3/bin/Caladesi.Cli.SelfHost/$pivot"
verified_tasks="$stage_root/stage3/bin/Caladesi.Build.Tasks.SelfHost/$pivot/$task_name.dll"
for pair in "Compiler:$compiler_name" "Runtime:$runtime_name"; do
  project="${pair%%:*}"
  name="${pair#*:}"
  cmp -s "$verified_cli/$name.dll" "$stage_root/stage3/bin/Caladesi.$project.SelfHost/$pivot/$name.dll" \
    || fail "Verified CLI dependency copy differs from its fixed-point artifact: $name"
done

work="$root/artifacts/aot-check/$rid/$identity/$(date -u +%Y%m%dT%H%M%SZ)-$$"
[[ "$stage_root" != "$work" && "$stage_root" != "$work/"* && "$work" != "$stage_root/"* ]] \
  || fail "AOT outputs must be separate from the verified native stages."
mkdir -p "$work"
common=(-c "$config" --nologo -p:Version="$version" -p:BuildProjectReferences=false -p:RestoreRecursive=false
  -p:UseArtifactsOutput=true -p:ArtifactsPath="$work" -p:ArtifactsPivots="$pivot"
  -p:CaladesiCompilerPath="$verified_cli/$cli_name.dll" -p:CaladesiTasksAssembly="$verified_tasks"
  -p:SelfHostReleaseIdentity="$identity" -p:CaladesiImplicitRuntimeReference=false
  -p:EnableAotAnalyzer=true -p:EnableTrimAnalyzer=true -p:TreatWarningsAsErrors=true
  -p:UseAppHost=false)
projects=(
  "Caladesi.Runtime.SelfHost"
  "Caladesi.Compiler.SelfHost"
  "Caladesi.Cli.SelfHost"
  "Caladesi.LanguageServer.SelfHost"
)
status=0
for name in "${projects[@]}"; do
  project="$root/src/$name/$name.calaproj"
  out="$work/publish/$name"
  log="$work/$name.log"
  echo "== AOT publish: $name"
  case "$name" in
    Caladesi.Compiler.SelfHost|Caladesi.Runtime.SelfHost)
      # Native libraries are also analyzed by the executable AOT publishers.
      dotnet build "$project" "${common[@]}" --no-incremental -p:PublishAot=false >"$log" 2>&1 \
        || { cat "$log"; status=1; break; }
      ;;
    *)
      dotnet publish "$project" "${common[@]}" -r "$rid" -o "$out" -p:PublishAot=true -p:UseAppHost=true >"$log" 2>&1 \
        || { cat "$log"; status=1; continue; }
      binary="$cli_name"
      if [[ "$name" == "Caladesi.LanguageServer.SelfHost" ]]; then binary="$server_name"; fi
      if [[ "$rid" == win-* ]]; then binary="$binary.exe"; fi
      if [[ ! -f "$out/$binary" ]]; then
        echo "Native AOT executable missing: $out/$binary" >&2
        status=1
      fi
      ;;
  esac
  if grep -E "warning (IL2|IL3)[0-9]{3}" "$log" >/dev/null; then
    echo "AOT/trim warnings in $name:"; grep -E "warning (IL2|IL3)[0-9]{3}" "$log" | sort -u; status=1
  fi
done
if [ $status -eq 0 ]; then echo "Native AOT check passed"; fi
exit $status
