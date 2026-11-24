#!/bin/bash
set -eu
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null || ! dotnet --version >/dev/null; then
    echo "Install the .NET 10 SDK for this Mac, then open Start-CSweet.command again." >&2
    exit 1
fi
if ! command -v docker >/dev/null; then
    # Docker Desktop can install its CLI for the current user only.
    export PATH="$HOME/.docker/bin:/Applications/Docker.app/Contents/Resources/bin:$PATH"
fi
if ! command -v docker >/dev/null; then
    echo "Install and open Docker Desktop for Mac, then try again." >&2
    exit 1
fi
if ! docker info >/dev/null 2>&1; then
    echo "Starting Docker Desktop and waiting for its Linux engine..."
    open -a Docker || exit 1
    for ((attempt=0; attempt<60; attempt++)); do
        docker info >/dev/null 2>&1 && break
        sleep 2
    done
fi
if [[ "$(docker info --format '{{.OSType}}')" != linux ]]; then
    echo "Docker's Linux engine must be running. Check docker info and try again." >&2
    exit 1
fi

profile=http
if dotnet dev-certs https --check --trust >/dev/null 2>&1; then
    profile=https
else
    echo "Using local HTTP. For trusted HTTPS, run dotnet dev-certs https --trust and restart."
fi
echo "Keep this terminal open. Open the app endpoint in the Aspire dashboard. Press Control-C to stop."
exec dotnet run --project src/CSweet.AppHost/CSweet.AppHost.csproj --launch-profile "$profile"
