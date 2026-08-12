#!/bin/bash
set -euo pipefail

if [ "$#" -lt 13 ] || [ "$#" -gt 14 ]; then
  echo "usage: $0 OUTPUT_ROOT RID FIRECRACKER JAILER VMLINUX INITRD GUEST_EXT4 GUEST_SIG SIGNING_CERT CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]" >&2
  exit 2
fi
for command_name in dotnet jq sha256sum; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required." >&2; exit 2; }
done

output_root=$(realpath -m "$1")
runtime_id=$2
firecracker=$(realpath "$3")
jailer=$(realpath "$4")
kernel=$(realpath "$5")
initrd=$(realpath "$6")
guest=$(realpath "$7")
guest_signature=$(realpath "$8")
signing_certificate=$(realpath "$9")
certificate_thumbprint=${10}
evidence=$(realpath "${11}")
suite_version=${12}
certified_at=${13}
expires_at=${14:-}
case "$runtime_id" in
  linux-x64) architecture=x64 ;;
  linux-arm64) architecture=arm64 ;;
  *) echo "RID must be linux-x64 or linux-arm64." >&2; exit 2 ;;
esac
case "$certificate_thumbprint" in
  *[!0-9A-Fa-f]*|'') echo "The certificate thumbprint must be hexadecimal." >&2; exit 2 ;;
esac
firecracker_version=$("$firecracker" --version 2>&1 | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+' | head -n 1 || true)
jailer_version=$("$jailer" --version 2>&1 | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+' | head -n 1 || true)
if [ -z "$firecracker_version" ] || [ "$firecracker_version" != "$jailer_version" ]; then
  echo "Firecracker and jailer must be executable binaries from the same release." >&2; exit 2
fi
if [ "$(printf '%s\n' 1.14.0 "$firecracker_version" | sort -V | head -n 1)" != 1.14.0 ]; then
  echo "Firecracker 1.14.0 or later is required for bounded serial diagnostics." >&2; exit 2
fi
if [ -e "$output_root" ] && [ -n "$(find "$output_root" -mindepth 1 -print -quit 2>/dev/null)" ]; then
  echo "The output directory must be empty: $output_root" >&2; exit 2
fi

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(realpath "$script_root/../..")
build_root=$(mktemp -d)
trap 'rm -rf -- "$build_root"' EXIT
mkdir -p "$output_root/firecracker" "$output_root/images" "$output_root/certificates" "$output_root/certification"

dotnet publish "$repository_root/src/CSweet.RuntimeHost/CSweet.RuntimeHost.csproj" -c Release -r "$runtime_id" --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -o "$build_root/runtime-host"
dotnet publish "$repository_root/src/CSweet.ExecutionNode/CSweet.ExecutionNode.csproj" -c Release -r "$runtime_id" --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -o "$build_root/execution-node"
dotnet publish "$repository_root/src/CSweet.AgentRuntime.Firecracker.Helper/CSweet.AgentRuntime.Firecracker.Helper.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o "$build_root/helper"

install -m 0755 "$build_root/runtime-host/CSweet.RuntimeHost" "$output_root/CSweet.RuntimeHost"
install -m 0755 "$build_root/execution-node/CSweet.ExecutionNode" "$output_root/CSweet.ExecutionNode"
install -m 0755 "$build_root/helper/CSweet.AgentRuntime.Firecracker.Helper" "$output_root/CSweet.AgentRuntime.Firecracker.Helper"
install -m 0755 "$firecracker" "$output_root/firecracker/firecracker"
install -m 0755 "$jailer" "$output_root/firecracker/jailer"
install -m 0644 "$kernel" "$output_root/firecracker/vmlinux"
install -m 0644 "$initrd" "$output_root/firecracker/initrd.img"
install -m 0644 "$guest" "$output_root/images/csweet-agent-guest.ext4"
install -m 0644 "$guest_signature" "$output_root/images/csweet-agent-guest.ext4.sig"
install -m 0644 "$signing_certificate" "$output_root/certificates/guest-image-signing.cer"
install -m 0644 "$evidence" "$output_root/certification/linux-firecracker.json"
install -m 0755 "$script_root/install-execution-node.sh" "$output_root/install-execution-node.sh"
install -m 0755 "$script_root/uninstall-execution-node.sh" "$output_root/uninstall-execution-node.sh"
install -m 0644 "$script_root/csweet-runtime-host.service" "$output_root/csweet-runtime-host.service"
install -m 0644 "$script_root/csweet-execution-node.service" "$output_root/csweet-execution-node.service"

files_json="$build_root/files.jsonl"
while IFS= read -r -d '' file; do
  relative=${file#"$output_root/"}
  digest=$(sha256sum "$file" | awk '{print $1}')
  jq -cn --arg path "$relative" --arg sha "$digest" '{path:$path,sha256:$sha}' >> "$files_json"
done < <(find "$output_root" -type f ! -name runtime-manifest.json -print0 | sort -z)

guest_digest="sha256:$(sha256sum "$output_root/images/csweet-agent-guest.ext4" | awk '{print $1}')"
evidence_digest="sha256:$(sha256sum "$output_root/certification/linux-firecracker.json" | awk '{print $1}')"
if [ -n "$expires_at" ]; then expiration=$(jq -cn --arg value "$expires_at" '$value'); else expiration=null; fi
jq -s \
  --arg providerVersion "1.0.0" --arg architecture "$architecture" \
  --arg guestDigest "$guest_digest" --arg thumbprint "$certificate_thumbprint" \
  --arg suite "$suite_version" --arg evidenceDigest "$evidence_digest" \
  --arg certifiedAt "$certified_at" --argjson expiration "$expiration" \
  '{schemaVersion:1,providerId:"firecracker-kvm",providerVersion:$providerVersion,
    hostOperatingSystem:"linux",hostArchitecture:$architecture,
    helperExecutable:"CSweet.AgentRuntime.Firecracker.Helper",
    guestImage:"images/csweet-agent-guest.ext4",guestImageDigest:$guestDigest,
    guestImageSignature:"images/csweet-agent-guest.ext4.sig",
    guestImageSigningCertificate:"certificates/guest-image-signing.cer",
    guestImageSigningCertificateThumbprint:$thumbprint,brokerProtocolVersion:"1.0",
    certificationSuiteVersion:$suite,certificationEvidence:"certification/linux-firecracker.json",
    certificationEvidenceDigest:$evidenceDigest,certifiedAt:$certifiedAt,
    certificationExpiresAt:$expiration,files:.}' \
  "$files_json" > "$output_root/runtime-manifest.json"
chmod 0644 "$output_root/runtime-manifest.json"
echo "Linux Firecracker execution payload created at $output_root"
