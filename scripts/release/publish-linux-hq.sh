#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
out=$(realpath -m "${1:?Supply an empty output directory}")
mkdir -p "$out"
[[ -z "$(ls -A "$out")" ]] || { echo "Output must be empty" >&2; exit 1; }
stage=$(mktemp -d)
mkdir "$stage/feed"
cat > "$stage/NuGet.Config" <<EOF
<configuration><packageSources><clear/><add key="local" value="$stage/feed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
EOF
props=("-p:RestoreConfigFile=$stage/NuGet.Config" -p:UseLocalIsolation=false -p:UseLocalOfficeContracts=false -p:UseLocalCSweetAgentSdk=false -p:UseLocalWorkManagementContracts=false)
# Same immutable prerequisite revisions as the Windows release.
while IFS=$'\t' read -r name repo commit project version; do
  dep="$stage/$name"
  git init -q "$dep"
  git -C "$dep" fetch -q --depth 1 "$repo" "$commit"
  git -C "$dep" checkout -q --detach FETCH_HEAD
  dotnet pack "$dep/$project" -c Release -o "$stage/feed" "${props[@]}" -p:ManagePackageVersionsCentrally=false
  test -s "$stage/feed/$name.$version.nupkg"
done < <(jq -r '.[] | [.package,.repository,.commit,.project,.version] | @tsv' scripts/release/build-dependencies.json)
for service in Api AgentHost WorkerHost ExecutionGateway Migrator GitHost SourceControlProvisionerHost; do
  name="CSweet.$service"
  dotnet publish "src/$name/$name.csproj" -c Release -r linux-x64 --self-contained false -p:UseAppHost=false "${props[@]}" -o "$out/$name"
  test -s "$out/$name/$name.dll"
done
dotnet publish src/CSweet.App/CSweet.App.csproj -c Release "${props[@]}" -o "$stage/ui"
cp -R "$stage/ui/wwwroot" "$out/web"
test -s "$out/web/index.html"
find "$out" -name appsettings.Development.json -type f -delete
cp docker/hosted-headquarters.Dockerfile "$out/Dockerfile"
