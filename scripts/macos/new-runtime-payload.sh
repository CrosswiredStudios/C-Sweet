#!/bin/bash
set -euo pipefail

if [ "$#" -lt 11 ] || [ "$#" -gt 12 ]; then
  echo "usage: $0 OUTPUT_ROOT RID SIGNING_IDENTITY VMLINUX GUEST_IMG GUEST_SIG SIGNING_CERT CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]" >&2
  exit 2
fi
for command_name in codesign dotnet jq plutil python3 shasum swift; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required." >&2; exit 2; }
done

output_root=$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$1")
runtime_id=$2
signing_identity=$3
kernel=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$4")
guest=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$5")
guest_signature=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$6")
signing_certificate=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$7")
certificate_thumbprint=$8
evidence=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$9")
suite_version=${10}
certified_at=${11}
expires_at=${12:-}
case "$runtime_id" in
  osx-arm64) architecture=arm64; swift_arch=arm64 ;;
  osx-x64) architecture=x64; swift_arch=x86_64 ;;
  *) echo "RID must be osx-arm64 or osx-x64." >&2; exit 2 ;;
esac
case "$certificate_thumbprint" in
  *[!0-9A-Fa-f]*|'') echo "The certificate thumbprint must be hexadecimal." >&2; exit 2 ;;
esac
if [ -e "$output_root" ] && [ -n "$(find "$output_root" -mindepth 1 -print -quit 2>/dev/null)" ]; then
  echo "The output directory must be empty: $output_root" >&2; exit 2
fi

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(cd "$script_root/../.." && pwd)
helper_root="$repository_root/src/CSweet.AgentRuntime.AppleVirtualization.Helper"
build_root=$(mktemp -d)
trap 'rm -rf -- "$build_root"' EXIT
mkdir -p "$output_root/apple-virtualization" "$output_root/images" "$output_root/certificates" "$output_root/certification"

dotnet publish "$repository_root/src/CSweet.RuntimeHost/CSweet.RuntimeHost.csproj" -c Release -r "$runtime_id" --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -o "$build_root/runtime-host"
dotnet publish "$repository_root/src/CSweet.ExecutionNode/CSweet.ExecutionNode.csproj" -c Release -r "$runtime_id" --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -o "$build_root/execution-node"
swift build --package-path "$helper_root" -c release --arch "$swift_arch"
helper_bin=$(swift build --package-path "$helper_root" -c release --arch "$swift_arch" --show-bin-path)/CSweet.AgentRuntime.AppleVirtualization.Helper

install -m 0755 "$build_root/runtime-host/CSweet.RuntimeHost" "$output_root/CSweet.RuntimeHost"
install -m 0755 "$build_root/execution-node/CSweet.ExecutionNode" "$output_root/CSweet.ExecutionNode"
install -m 0755 "$helper_bin" "$output_root/CSweet.AgentRuntime.AppleVirtualization.Helper"
install -m 0644 "$kernel" "$output_root/apple-virtualization/vmlinux"
install -m 0644 "$guest" "$output_root/images/csweet-agent-guest.img"
install -m 0644 "$guest_signature" "$output_root/images/csweet-agent-guest.img.sig"
install -m 0644 "$signing_certificate" "$output_root/certificates/guest-image-signing.cer"
install -m 0644 "$evidence" "$output_root/certification/macos-apple-virtualization.json"
install -m 0755 "$script_root/install-execution-node.sh" "$output_root/install-execution-node.sh"
install -m 0755 "$script_root/uninstall-execution-node.sh" "$output_root/uninstall-execution-node.sh"
install -m 0644 "$script_root/com.csweet.runtimehost.plist" "$output_root/com.csweet.runtimehost.plist"
install -m 0644 "$script_root/com.csweet.executionnode.plist" "$output_root/com.csweet.executionnode.plist"

codesign --force --timestamp --options runtime --sign "$signing_identity" \
  --entitlements "$helper_root/CSweet.AppleVirtualization.entitlements" \
  "$output_root/CSweet.AgentRuntime.AppleVirtualization.Helper"
codesign --force --timestamp --options runtime --sign "$signing_identity" "$output_root/CSweet.RuntimeHost"
codesign --force --timestamp --options runtime --sign "$signing_identity" "$output_root/CSweet.ExecutionNode"
codesign --verify --strict "$output_root/CSweet.AgentRuntime.AppleVirtualization.Helper"
entitlement_value=$(codesign -d --entitlements :- "$output_root/CSweet.AgentRuntime.AppleVirtualization.Helper" 2>/dev/null | \
  plutil -extract com.apple.security.virtualization raw -o - -)
[ "$entitlement_value" = "true" ] || { echo "The helper is missing the virtualization entitlement." >&2; exit 2; }

files_json="$build_root/files.jsonl"
while IFS= read -r -d '' file; do
  relative=${file#"$output_root/"}
  digest=$(shasum -a 256 "$file" | awk '{print $1}')
  jq -cn --arg path "$relative" --arg sha "$digest" '{path:$path,sha256:$sha}' >> "$files_json"
done < <(find "$output_root" -type f ! -name runtime-manifest.json -print0 | sort -z)

guest_digest="sha256:$(shasum -a 256 "$output_root/images/csweet-agent-guest.img" | awk '{print $1}')"
evidence_digest="sha256:$(shasum -a 256 "$output_root/certification/macos-apple-virtualization.json" | awk '{print $1}')"
if [ -n "$expires_at" ]; then expiration=$(jq -cn --arg value "$expires_at" '$value'); else expiration=null; fi
jq -s \
  --arg architecture "$architecture" --arg guestDigest "$guest_digest" \
  --arg thumbprint "$certificate_thumbprint" --arg suite "$suite_version" \
  --arg evidenceDigest "$evidence_digest" --arg certifiedAt "$certified_at" \
  --argjson expiration "$expiration" \
  '{schemaVersion:1,providerId:"apple-virtualization",providerVersion:"1.0.0",
    hostOperatingSystem:"macos",hostArchitecture:$architecture,
    helperExecutable:"CSweet.AgentRuntime.AppleVirtualization.Helper",
    guestImage:"images/csweet-agent-guest.img",guestImageDigest:$guestDigest,
    guestImageSignature:"images/csweet-agent-guest.img.sig",
    guestImageSigningCertificate:"certificates/guest-image-signing.cer",
    guestImageSigningCertificateThumbprint:$thumbprint,brokerProtocolVersion:"1.0",
    certificationSuiteVersion:$suite,certificationEvidence:"certification/macos-apple-virtualization.json",
    certificationEvidenceDigest:$evidenceDigest,certifiedAt:$certifiedAt,
    certificationExpiresAt:$expiration,files:.}' \
  "$files_json" > "$output_root/runtime-manifest.json"
chmod 0644 "$output_root/runtime-manifest.json"
echo "macOS Apple Virtualization execution payload created at $output_root"
