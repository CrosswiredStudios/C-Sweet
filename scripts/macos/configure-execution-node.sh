#!/bin/sh
set -eu

package_root='/Library/Application Support/CSweet/InstallerPayload'
installer="$package_root/install-execution-node.sh"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this command with sudo." >&2
  exit 1
fi
if [ "$#" -ne 1 ]; then
  echo "usage: sudo $0 https://control-plane" >&2
  exit 2
fi
case "$1" in
  https://*) ;;
  *) echo "Control-plane URL must use HTTPS." >&2; exit 2 ;;
esac
if [ ! -x "$installer" ]; then
  echo "The signed C-Sweet execution payload is not installed." >&2
  exit 2
fi

exec "$installer" "$package_root" "$1"
