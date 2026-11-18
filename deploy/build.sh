#!/usr/bin/env bash
# Builds a release archive for the Linux server: Api, Worker and DevTools, each self-contained for linux-x64
# (the server has no .NET runtime). Runs on the developer machine (Git Bash on Windows, or Linux/macOS).
#
#   deploy/build.sh                 -> artifacts/synect-<UTC date>-<commit>.tar.gz
#
# The archive holds no secrets: connection strings and keys live only on the server (/etc/synect/*.env).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"

commit="$(git rev-parse --short=12 HEAD)"
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  commit="${commit}-dirty"
  echo "WARNING: uncommitted changes are included in this build ($commit)." >&2
fi
stamp="$(date -u +%Y%m%d-%H%M%S)"
name="synect-${stamp}-${commit}"
out="$repo/artifacts/$name"

rm -rf "$out"
mkdir -p "$out"

for project in Api Worker DevTools; do
  dir="$(echo "$project" | tr '[:upper:]' '[:lower:]')"
  echo "== dotnet publish Synect.$project -> $dir/"
  dotnet publish "src/Synect.$project/Synect.$project.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:SourceRevisionId="$commit" \
    -p:DebugType=none -p:GenerateDocumentationFile=false \
    -o "$out/$dir" -nologo -v q
done

# Development settings and user-secrets ids must not travel to the server.
find "$out" -name 'appsettings.Development.json' -delete

cat > "$out/VERSION" <<EOF
release=$name
commit=$commit
built_utc=$stamp
EOF

tar -C "$repo/artifacts" -czf "$repo/artifacts/$name.tar.gz" "$name"
rm -rf "$out"
echo "Built artifacts/$name.tar.gz ($(du -h "$repo/artifacts/$name.tar.gz" | cut -f1))"
