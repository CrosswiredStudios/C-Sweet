#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then echo "Run this installer with sudo." >&2; exit 1; fi
if [ "$#" -lt 2 ]; then echo "usage: $0 PACKAGE_ROOT https://control-plane [--enrollment-token-file PATH] [--result-job-id ID]" >&2; exit 2; fi
package_root=$1
control_plane=$2
shift 2
token_file=
result_job_id=
result_file=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --enrollment-token-file) [ "$#" -ge 2 ] || exit 2; token_file=$2; shift 2 ;;
    --result-job-id) [ "$#" -ge 2 ] || exit 2; result_job_id=$2; shift 2 ;;
    *) echo "Unknown installer option: $1" >&2; exit 2 ;;
  esac
done
if [ -n "$result_job_id" ]; then
  case "$result_job_id" in *[!0-9a-f]*|'') echo "The installer result job ID is invalid." >&2; exit 2;; esac
  [ "${#result_job_id}" -eq 32 ] || { echo "The installer result job ID is invalid." >&2; exit 2; }
  install -d -o root -g wheel -m 0755 /Library/Application\ Support/CSweet/Setup
  result_file=/Library/Application\ Support/CSweet/Setup/local-provisioning-$result_job_id.result
fi
write_result() {
  if [ -n "$result_file" ]; then printf '%s\n' "$1" > "$result_file"; chmod 0644 "$result_file"; fi
}
trap 'write_result failed' 0
case "$control_plane" in https://*) ;; *) echo "Control-plane URL must use HTTPS." >&2; exit 2;; esac
codesign --verify --strict --deep "$package_root/CSweet.RuntimeHost"
codesign --verify --strict --deep "$package_root/CSweet.ExecutionNode"
codesign --verify --strict "$package_root/CSweet.AgentRuntime.AppleVirtualization.Helper"
entitlement_value=$(codesign -d --entitlements :- "$package_root/CSweet.AgentRuntime.AppleVirtualization.Helper" 2>/dev/null | \
  plutil -extract com.apple.security.virtualization raw -o - -)
if [ "$entitlement_value" != "true" ]; then
  echo "The signed helper is missing the Apple Virtualization entitlement." >&2; exit 2
fi
if [ ! -f "$package_root/runtime-manifest.json" ]; then
  echo "The signed Apple Virtualization runtime manifest is missing." >&2; exit 2
fi
if [ ! -f "$package_root/apple-virtualization/vmlinux" ]; then
  echo "The pinned Apple Virtualization guest kernel is missing." >&2; exit 2
fi
if find "$package_root" -type l -print -quit | grep -q .; then
  echo "Execution packages may not contain symbolic links." >&2; exit 2
fi
if [ -f /Library/LaunchDaemons/com.csweet.executionnode.plist ] ||
   [ -e /Library/Application\ Support/CSweet/Execution/CSweet.ExecutionNode ]; then
  maintenance=/Library/Application\ Support/CSweet/ExecutionNode/maintenance
  drain_state=
  [ ! -f "$maintenance/drain-state" ] || drain_state=$(tr -d '\r\n' < "$maintenance/drain-state")
  active_count=0
  if [ -d "$maintenance/active-assignments" ]; then
    active_count=$(find "$maintenance/active-assignments" -type f -name '*.active' | wc -l | tr -d ' ')
  fi
  if [ "$drain_state" != draining ] || [ "$active_count" -ne 0 ]; then
    echo "Drain this node in C-Sweet and wait for active assignments to reach zero before upgrading." >&2
    exit 3
  fi
  launchctl bootout system/com.csweet.executionnode 2>/dev/null || true
  launchctl bootout system/com.csweet.runtimehost 2>/dev/null || true
fi

if ! dscl . -read /Groups/_csweet >/dev/null 2>&1; then
  service_gid=498
  while dscl . -search /Groups PrimaryGroupID "$service_gid" 2>/dev/null | grep -q .; do
    service_gid=$((service_gid - 1))
    [ "$service_gid" -ge 350 ] || { echo "No protected service group ID is available." >&2; exit 2; }
  done
  dscl . -create /Groups/_csweet
  dscl . -create /Groups/_csweet RealName "C-Sweet execution services"
  dscl . -create /Groups/_csweet PrimaryGroupID "$service_gid"
fi
service_gid=$(dscl . -read /Groups/_csweet PrimaryGroupID | awk '{print $2}')
case "$service_gid" in *[!0-9]*|'') echo "The C-Sweet service group is invalid." >&2; exit 2;; esac
if ! dscl . -read /Users/_csweetnode >/dev/null 2>&1; then
  service_uid=498
  while dscl . -search /Users UniqueID "$service_uid" 2>/dev/null | grep -q .; do
    service_uid=$((service_uid - 1))
    [ "$service_uid" -ge 350 ] || { echo "No protected service user ID is available." >&2; exit 2; }
  done
  dscl . -create /Users/_csweetnode
  dscl . -create /Users/_csweetnode RealName "C-Sweet Execution Node"
  dscl . -create /Users/_csweetnode UniqueID "$service_uid"
  dscl . -create /Users/_csweetnode PrimaryGroupID "$service_gid"
  dscl . -create /Users/_csweetnode UserShell /usr/bin/false
  dscl . -create /Users/_csweetnode NFSHomeDirectory /var/empty
  dscl . -create /Users/_csweetnode IsHidden 1
fi
node_gid=$(dscl . -read /Users/_csweetnode PrimaryGroupID | awk '{print $2}')
if [ "$node_gid" != "$service_gid" ]; then
  echo "The existing _csweetnode identity does not belong to the protected C-Sweet group." >&2; exit 2
fi

if [ -n "$token_file" ]; then
  [ -f "$token_file" ] && [ ! -L "$token_file" ] || { echo "The protected enrollment input is invalid." >&2; exit 2; }
  token=$(tr -d '\r\n' < "$token_file")
  rm -f -- "$token_file"
elif [ -t 0 ]; then printf 'Enrollment token: ' >&2; stty -echo; IFS= read -r token; stty echo; printf '\n' >&2
else IFS= read -r token; fi
if [ "${#token}" -lt 32 ] || [ "${#token}" -gt 256 ]; then echo "Invalid enrollment token." >&2; exit 2; fi

install -d -m 0755 /Library/Application\ Support/CSweet/Execution /Library/Application\ Support/CSweet/ExecutionNode /Library/Application\ Support/CSweet/ArtifactMedia /Library/Application\ Support/CSweet/AgentRuntime
install -d -m 0700 /Library/Application\ Support/CSweet/AgentRuntime/AppleVirtualization /Library/Application\ Support/CSweet/AgentRuntime/AppleVirtualization/instances
install -d -m 0700 /var/run/csweet-av
ditto "$package_root" /Library/Application\ Support/CSweet/Execution
chown -R root:wheel /Library/Application\ Support/CSweet/Execution
chmod 0755 /Library/Application\ Support/CSweet/Execution/CSweet.RuntimeHost /Library/Application\ Support/CSweet/Execution/CSweet.ExecutionNode /Library/Application\ Support/CSweet/Execution/CSweet.AgentRuntime.AppleVirtualization.Helper
chmod 0644 /Library/Application\ Support/CSweet/Execution/runtime-manifest.json
chown -R root:wheel /Library/Application\ Support/CSweet/AgentRuntime/AppleVirtualization
chmod 0700 /Library/Application\ Support/CSweet/AgentRuntime/AppleVirtualization /Library/Application\ Support/CSweet/AgentRuntime/AppleVirtualization/instances
chown root:wheel /var/run/csweet-av
chmod 0700 /var/run/csweet-av
if [ ! -f /Library/Application\ Support/CSweet/AgentRuntime/runtime-host.key ]; then
  umask 027
  dd if=/dev/urandom bs=32 count=1 2>/dev/null | base64 > /Library/Application\ Support/CSweet/AgentRuntime/runtime-host.key
fi
chown root:_csweet /Library/Application\ Support/CSweet/AgentRuntime/runtime-host.key
chmod 0640 /Library/Application\ Support/CSweet/AgentRuntime/runtime-host.key
printf '%s' "$token" > /Library/Application\ Support/CSweet/ExecutionNode/enrollment.secret
unset token
chmod 0600 /Library/Application\ Support/CSweet/ExecutionNode/enrollment.secret
chown -R _csweetnode:_csweet /Library/Application\ Support/CSweet/ExecutionNode
chown -R _csweetnode:_csweet /Library/Application\ Support/CSweet/ArtifactMedia
chmod 0770 /Library/Application\ Support/CSweet/ArtifactMedia
install -m 0644 "$package_root/com.csweet.runtimehost.plist" /Library/LaunchDaemons/com.csweet.runtimehost.plist
escaped_control_plane=$(printf '%s' "$control_plane" | sed 's/[&|\\]/\\&/g')
sed "s|__CONTROL_PLANE_URL__|$escaped_control_plane|g" "$package_root/com.csweet.executionnode.plist" > /Library/LaunchDaemons/com.csweet.executionnode.plist
chmod 0644 /Library/LaunchDaemons/com.csweet.executionnode.plist
launchctl bootstrap system /Library/LaunchDaemons/com.csweet.runtimehost.plist
launchctl bootstrap system /Library/LaunchDaemons/com.csweet.executionnode.plist
write_result completed
trap - 0
