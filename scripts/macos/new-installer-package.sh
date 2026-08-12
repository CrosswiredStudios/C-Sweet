#!/bin/bash
set -euo pipefail

if [ "$#" -ne 5 ]; then
  echo "usage: $0 PAYLOAD_ROOT OUTPUT_PKG VERSION INSTALLER_SIGNING_IDENTITY NOTARY_KEYCHAIN_PROFILE" >&2
  exit 2
fi

for command_name in codesign ditto pkgbuild pkgutil plutil productbuild python3 spctl xcrun; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required." >&2; exit 2; }
done

payload_root=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1")
output_pkg=$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$2")
version=$3
signing_identity=$4
notary_profile=$5
printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || {
  echo "VERSION must use the form MAJOR.MINOR.PATCH." >&2; exit 2;
}
[ ! -e "$output_pkg" ] || { echo "Output already exists: $output_pkg" >&2; exit 2; }

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
for required in runtime-manifest.json install-execution-node.sh uninstall-execution-node.sh \
  com.csweet.runtimehost.plist com.csweet.executionnode.plist CSweet.RuntimeHost CSweet.ExecutionNode \
  CSweet.AgentRuntime.AppleVirtualization.Helper; do
  [ -e "$payload_root/$required" ] || { echo "Payload is missing $required." >&2; exit 2; }
done
[ -f "$script_root/configure-execution-node.sh" ] || { echo "configure-execution-node.sh is missing." >&2; exit 2; }
if find "$payload_root" -type l -print -quit | grep -q .; then
  echo "Execution packages may not contain symbolic links." >&2
  exit 2
fi

codesign --verify --strict --deep "$payload_root/CSweet.RuntimeHost"
codesign --verify --strict --deep "$payload_root/CSweet.ExecutionNode"
codesign --verify --strict "$payload_root/CSweet.AgentRuntime.AppleVirtualization.Helper"
entitlement_value=$(codesign -d --entitlements :- "$payload_root/CSweet.AgentRuntime.AppleVirtualization.Helper" 2>/dev/null | \
  plutil -extract com.apple.security.virtualization raw -o - -)
[ "$entitlement_value" = true ] || { echo "The helper is missing the Apple Virtualization entitlement." >&2; exit 2; }

build_root=$(mktemp -d)
trap 'rm -rf -- "$build_root"' EXIT
package_root="$build_root/root"
staged_payload="$package_root/Library/Application Support/CSweet/InstallerPayload"
mkdir -p "$staged_payload" "$package_root/usr/local/sbin" "$(dirname "$output_pkg")"
ditto "$payload_root" "$staged_payload"
install -m 0755 "$script_root/configure-execution-node.sh" \
  "$package_root/usr/local/sbin/csweet-configure-execution-node"
install -m 0755 "$script_root/uninstall-execution-node.sh" \
  "$package_root/usr/local/sbin/csweet-uninstall-execution-node"

component_pkg="$build_root/csweet-execution-node-component.pkg"
pkgbuild --root "$package_root" --identifier com.csweet.execution-node.payload \
  --version "$version" --install-location / --ownership recommended "$component_pkg"
productbuild --package "$component_pkg" --sign "$signing_identity" "$output_pkg"
pkgutil --check-signature "$output_pkg"
xcrun notarytool submit "$output_pkg" --keychain-profile "$notary_profile" --wait
xcrun stapler staple "$output_pkg"
xcrun stapler validate "$output_pkg"
spctl --assess --type install --verbose "$output_pkg"

echo "Created signed and notarized macOS installer at $output_pkg"
