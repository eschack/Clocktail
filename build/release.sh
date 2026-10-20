#!/usr/bin/env bash
# Builds the release: three self-contained single-file executables, the npm package (the setup genie plus the server
# and the service) and a zip for manual installs. Usage: build/release.sh 0.1.0   (output in ./out)
set -euo pipefail
ver="${1:?version, e.g. 0.1.0}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/out"
rm -rf "$out" && mkdir -p "$out/bin"
for p in src/Clocktail.Broker tools/Clocktail.Mcp src/Clocktail.Cli; do
  dotnet publish "$root/$p" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version="$ver" -o "$out/bin" -nologo -v quiet
done
mv "$out/bin/Clocktail.Cli.exe" "$out/bin/clocktail.exe"

# npm: the genie ships the server and the service (the CLI stays in the zip).
pkg="$out/npm"
cp -r "$root/npm" "$pkg"
mkdir -p "$pkg/dist"
cp "$out/bin/clocktail-mcp.exe" "$out/bin/Clocktail.Broker.exe" "$pkg/dist/"
cp "$root/README.md" "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" "$pkg/"
(cd "$pkg" && npm pkg set version="$ver" >/dev/null && npm pack --pack-destination "$out" >/dev/null)

# zip: everything, for people who would rather not use npx.
(cd "$out/bin" && cp "$root/README.md" "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" . && powershell -NoProfile -Command \
  "Compress-Archive -Path * -DestinationPath '$(cygpath -w "$out")\Clocktail-$ver-win-x64.zip' -Force")
ls -la "$out"
