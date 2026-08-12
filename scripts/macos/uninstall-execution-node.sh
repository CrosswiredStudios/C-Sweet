#!/bin/sh
set -eu

force=false
if [ "$#" -gt 1 ]; then echo "usage: $0 [--force]" >&2; exit 2; fi
if [ "$#" -eq 1 ]; then
  [ "$1" = "--force" ] || { echo "usage: $0 [--force]" >&2; exit 2; }
  force=true
fi
if [ "$(id -u)" -ne 0 ]; then echo "Run this uninstaller with sudo." >&2; exit 1; fi

maintenance=/Library/Application\ Support/CSweet/ExecutionNode/maintenance
drain_state=
[ ! -f "$maintenance/drain-state" ] || drain_state=$(tr -d '\r\n' < "$maintenance/drain-state")
active_count=0
if [ -d "$maintenance/active-assignments" ]; then
  active_count=$(find "$maintenance/active-assignments" -type f -name '*.active' | wc -l | tr -d ' ')
fi
if [ "$force" != true ] && { [ "$drain_state" != draining ] || [ "$active_count" -ne 0 ]; }; then
  echo "Drain this node in C-Sweet and wait for active assignments to reach zero before uninstalling." >&2
  echo "Use --force only after the node is revoked and active workloads may be terminated." >&2
  exit 3
fi

launchctl bootout system/com.csweet.executionnode 2>/dev/null || true
launchctl bootout system/com.csweet.runtimehost 2>/dev/null || true
rm -f -- /Library/LaunchDaemons/com.csweet.executionnode.plist
rm -f -- /Library/LaunchDaemons/com.csweet.runtimehost.plist
rm -rf -- /Library/Application\ Support/CSweet/Execution
rm -rf -- /Library/Application\ Support/CSweet/ExecutionNode
rm -rf -- /Library/Application\ Support/CSweet/ArtifactMedia
rm -rf -- /Library/Application\ Support/CSweet/AgentRuntime
rm -rf -- /Library/Application\ Support/CSweet/InstallerPayload
rmdir /Library/Application\ Support/CSweet 2>/dev/null || true
rm -rf -- /var/run/csweet-av

if dscl . -read /Users/_csweetnode >/dev/null 2>&1; then dscl . -delete /Users/_csweetnode; fi
if dscl . -read /Groups/_csweet >/dev/null 2>&1; then dscl . -delete /Groups/_csweet; fi

rm -f -- /usr/local/sbin/csweet-configure-execution-node
rm -f -- /usr/local/sbin/csweet-uninstall-execution-node
pkgutil --forget com.csweet.execution-node.payload >/dev/null 2>&1 || true

echo "C-Sweet RuntimeHost and ExecutionNode were uninstalled. Revoke the node in fleet administration if it was not already revoked."
