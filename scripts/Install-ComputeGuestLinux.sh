#!/usr/bin/env bash
# Run inside the disposable Linux Hyper-V guest, never on the provider host.
set -euo pipefail
[[ $# == 1 || ( $# == 2 && $2 == --image-build ) ]] || { echo "Usage: $0 /absolute/path/to/linux-guest-payload [--image-build]" >&2; exit 2; }
[[ $EUID == 0 && -d /sys/bus/vmbus && -d /run/systemd/system ]] || {
  echo "Run as root inside a Linux Hyper-V guest with systemd." >&2; exit 1;
}
version_text="$(systemctl --version)"
read -r _ version _ <<< "${version_text%%$'\n'*}"
[[ $version =~ ^[0-9]+$ && $version -ge 254 ]] || { echo "systemd 254+ is required (Ubuntu 24.04 recommended)." >&2; exit 1; }
[[ $1 == /* && -f $1/CSweet.Compute.Guest ]] || { echo "An absolute self-contained Linux payload directory is required." >&2; exit 1; }
install_root=/opt/csweet/compute-guest
unit_path=/etc/systemd/system/csweet-compute-guest.service
[[ ! -e $install_root && ! -e $unit_path ]] || { echo "Compute guest is already installed; preserve the existing installation." >&2; exit 1; }
[[ -x /usr/bin/python3 ]] || { echo "Install python3 in this template before installing the Hello World guest payload." >&2; exit 1; }
modprobe hv_sock
install -d -m 0755 "$install_root" /var/lib/csweet-compute/work
cp -a -- "$1"/. "$install_root"/
chown -R root:root "$install_root"
chmod 0755 "$install_root/CSweet.Compute.Guest"
printf '%s\n' hv_sock > /etc/modules-load.d/csweet-compute.conf
cat > "$unit_path" <<'UNIT'
[Unit]
Description=C-Sweet isolated compute guest
After=systemd-modules-load.service
ConditionPathExists=/sys/bus/vmbus

[Service]
Type=simple
ExecStart=/opt/csweet/compute-guest/CSweet.Compute.Guest --serve-hyperv
WorkingDirectory=/var/lib/csweet-compute/work
RuntimeDirectory=csweet-compute
Environment=TMPDIR=/run/csweet-compute
Restart=on-failure
RestartSec=3
TimeoutStopSec=15

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
if [[ ${2:-} == --image-build ]]; then
  # The shared image builder seals build access before the first service start.
  systemctl enable csweet-compute-guest.service
else
  systemctl enable --now csweet-compute-guest.service
  systemctl --no-pager status csweet-compute-guest.service
fi
