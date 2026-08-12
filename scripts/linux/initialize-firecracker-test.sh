#!/bin/bash
set -euo pipefail

control_plane_url=
output_root=
firecracker_version=v1.16.1
skip_install=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --control-plane) [[ $# -ge 2 ]] || { echo "--control-plane requires a value." >&2; exit 2; }; control_plane_url=$2; shift 2 ;;
    --output-root) [[ $# -ge 2 ]] || { echo "--output-root requires a value." >&2; exit 2; }; output_root=$2; shift 2 ;;
    --firecracker-version) [[ $# -ge 2 ]] || { echo "--firecracker-version requires a value." >&2; exit 2; }; firecracker_version=$2; shift 2 ;;
    --skip-install) skip_install=true; shift ;;
    *) echo "usage: $0 [--control-plane https://host] [--output-root PATH] [--firecracker-version vX.Y.Z] [--skip-install]" >&2; exit 2 ;;
  esac
done
if [[ $(id -u) -ne 0 ]]; then echo "Run this workflow as root." >&2; exit 2; fi
if [[ $skip_install == false && $control_plane_url != https://* ]]; then
  echo "--control-plane with an HTTPS URL is required unless --skip-install is used." >&2; exit 2
fi
case "$firecracker_version" in v[0-9]*.[0-9]*.[0-9]*) ;; *) echo "The Firecracker version is invalid." >&2; exit 2;; esac
for command_name in curl dotnet jq openssl sha256sum tar; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required." >&2; exit 2; }
done
if [[ ! -r /sys/fs/cgroup/cgroup.controllers || ! -r /dev/kvm || ! -w /dev/kvm ]]; then
  echo "A cgroup v2 host with read/write /dev/kvm access is required." >&2; exit 2
fi

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(realpath "$script_root/../..")
if [[ -z "$output_root" ]]; then output_root="$repository_root/artifacts/linux-test"; fi
output_root=$(realpath -m "$output_root")
if [[ "$output_root" == / ]]; then echo "The output root cannot be the filesystem root." >&2; exit 2; fi
architecture=$(uname -m)
case "$architecture" in
  x86_64) runtime_id=linux-x64 ;;
  aarch64) runtime_id=linux-arm64 ;;
  *) echo "Firecracker development testing requires x86_64 or aarch64." >&2; exit 2 ;;
esac

run_id=$(date -u +%Y%m%d-%H%M%S)-$(openssl rand -hex 4)
run_root="$output_root/$run_id"
tools_root="$output_root/tools/$firecracker_version-$architecture"
cache_root="$output_root/cache"
guest_root="$run_root/guest"
smoke_root="$run_root/smoke"
payload_root="$run_root/payload"
mkdir -p "$run_root" "$cache_root"

archive="firecracker-$firecracker_version-$architecture.tgz"
archive_path="$cache_root/$archive"
checksum_path="$cache_root/$archive.sha256.txt"
release_url="https://github.com/firecracker-microvm/firecracker/releases/download/$firecracker_version"
if [[ ! -f "$archive_path" || ! -f "$checksum_path" ]]; then
  echo "Downloading Firecracker $firecracker_version and its published checksum..."
  curl --fail --location --proto '=https' --tlsv1.2 "$release_url/$archive" --output "$archive_path"
  curl --fail --location --proto '=https' --tlsv1.2 "$release_url/$archive.sha256.txt" --output "$checksum_path"
fi
(cd "$cache_root" && sha256sum --check --status "$archive.sha256.txt") || {
  echo "The Firecracker release archive failed its published checksum." >&2; exit 2;
}
if [[ ! -x "$tools_root/firecracker" || ! -x "$tools_root/jailer" ]]; then
  extraction_root=$(mktemp -d)
  trap 'rm -rf -- "$extraction_root"' EXIT
  tar -xzf "$archive_path" -C "$extraction_root" --no-same-owner
  release_root="$extraction_root/release-$firecracker_version-$architecture"
  install -d -m 0755 "$tools_root"
  install -m 0755 "$release_root/firecracker-$firecracker_version-$architecture" "$tools_root/firecracker"
  install -m 0755 "$release_root/jailer-$firecracker_version-$architecture" "$tools_root/jailer"
fi
"$tools_root/firecracker" --version
"$tools_root/jailer" --version

echo "Building the immutable Firecracker guest filesystem..."
"$script_root/new-firecracker-guest.sh" "$guest_root" "$runtime_id"

helper_publish="$run_root/helper"
smoke_publish="$run_root/smoke-runner"
mkdir -p "$helper_publish" "$smoke_publish" "$smoke_root" "$smoke_root/package"
dotnet publish "$repository_root/src/CSweet.AgentRuntime.Firecracker.Helper/CSweet.AgentRuntime.Firecracker.Helper.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o "$helper_publish"
dotnet publish "$repository_root/src/CSweet.AgentRuntime.WindowsSmokeTest/CSweet.AgentRuntime.WindowsSmokeTest.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o "$smoke_publish"
install -m 0755 "$tools_root/firecracker" "$smoke_root/package/firecracker"
install -m 0755 "$tools_root/jailer" "$smoke_root/package/jailer"
install -m 0644 "$guest_root/vmlinux" "$smoke_root/package/vmlinux"
install -m 0644 "$guest_root/initrd.img" "$smoke_root/package/initrd.img"

id csweet-vm >/dev/null 2>&1 || useradd --system --home /nonexistent --shell /usr/sbin/nologin csweet-vm
export CSWEET_FIRECRACKER_PACKAGE_ROOT="$smoke_root/package"
export CSWEET_FIRECRACKER_WORKLOAD_UID="$(id -u csweet-vm)"
export CSWEET_FIRECRACKER_WORKLOAD_GID="$(id -g csweet-vm)"
export CSWEET_FIRECRACKER_PARENT_CGROUP=csweet-certification
mkdir -p /sys/fs/cgroup/csweet-certification
for controller in cpu memory pids; do
  if grep -qw "$controller" /sys/fs/cgroup/cgroup.controllers; then
    echo "+$controller" > /sys/fs/cgroup/cgroup.subtree_control 2>/dev/null || true
  fi
done

evidence_path="$run_root/linux-firecracker.json"
echo "Running real no-network runtime and builder certification VMs..."
"$smoke_publish/CSweet.AgentRuntime.WindowsSmokeTest" \
  --provider firecracker \
  --helper "$helper_publish/CSweet.AgentRuntime.Firecracker.Helper" \
  --guest-image "$guest_root/csweet-agent-guest.ext4" \
  --probe "$guest_root/CSweet.AgentRuntime.GuestProbe" \
  --output-root "$smoke_root/output" \
  --evidence "$evidence_path"
jq -e '.checks | length > 0 and all(.[]; . == true)' "$evidence_path" >/dev/null || {
  echo "Firecracker certification evidence contains a failed check." >&2; exit 2;
}

key_path="$run_root/development-signing.key"
certificate_pem="$run_root/development-signing.pem"
certificate_der="$run_root/development-signing.cer"
signature_path="$run_root/csweet-agent-guest.ext4.sig"
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 365 \
  -subj "/CN=C-Sweet Linux Firecracker Development Guest Signer" \
  -keyout "$key_path" -out "$certificate_pem" >/dev/null 2>&1
chmod 0600 "$key_path"
openssl x509 -in "$certificate_pem" -outform der -out "$certificate_der"
openssl dgst -sha256 -sign "$key_path" -out "$signature_path" "$guest_root/csweet-agent-guest.ext4"
certificate_thumbprint=$(openssl x509 -in "$certificate_pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')
suite_version=$(jq -r '.certificationSuiteVersion' "$evidence_path")
certified_at=$(jq -r '.certifiedAt' "$evidence_path")
expires_at=$(jq -r '.certificationExpiresAt // empty' "$evidence_path")

"$script_root/new-runtime-payload.sh" \
  "$payload_root" "$runtime_id" "$tools_root/firecracker" "$tools_root/jailer" \
  "$guest_root/vmlinux" "$guest_root/initrd.img" "$guest_root/csweet-agent-guest.ext4" \
  "$signature_path" "$certificate_der" "$certificate_thumbprint" "$evidence_path" \
  "$suite_version" "$certified_at" "$expires_at"

if [[ $skip_install == false ]]; then
  echo "Installing RuntimeHost and ExecutionNode. Enter the one-use enrollment token when prompted."
  "$script_root/install-execution-node.sh" "$payload_root" "$control_plane_url"
fi

echo "Firecracker development certification completed."
echo "Evidence: $evidence_path"
echo "Payload: $payload_root"
