#!/bin/bash
set -euo pipefail

guest_source=/tmp/CSweet.AgentRuntime.Guest
builder_source=/tmp/CSweet.AgentRuntime.Builder
guest_root=/usr/lib/csweet/guest
builder_root=/usr/lib/csweet/builder
if [[ ! -x "$guest_source" || ! -x "$builder_source" ]]; then
  echo 'A published C-Sweet guest executable is missing.' >&2
  exit 2
fi

install -d -m 0755 "$guest_root" "$builder_root"
install -o root -g root -m 0755 "$guest_source" "$guest_root/CSweet.AgentRuntime.Guest"
install -o root -g root -m 0755 "$builder_source" "$builder_root/CSweet.AgentRuntime.Builder"
getent group csweet-workload >/dev/null || groupadd --system csweet-workload
id csweet-workload >/dev/null 2>&1 || useradd --system --gid csweet-workload \
  --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin csweet-workload

cat >/usr/lib/csweet/prepare-runtime.sh <<'SCRIPT'
#!/bin/bash
set -euo pipefail

scratch=/dev/vdb
if [[ ! -b "$scratch" ]] || [[ "$(blockdev --getro "$scratch")" != 0 ]]; then
  echo 'The fixed writable scratch device is missing or read-only.' >&2
  exit 3
fi
if findmnt -rn -S "$scratch" >/dev/null; then
  echo 'The disposable scratch device is already mounted.' >&2
  exit 4
fi
if [[ -b /dev/vdc ]] && [[ "$(blockdev --getro /dev/vdc)" != 1 ]]; then
  echo 'The runtime artifact device is unexpectedly writable.' >&2
  exit 5
fi

install -d -o root -g root -m 0700 /run/csweet
wipefs --all --force "$scratch"
mkfs.ext4 -F -L CSWEET_SCRATCH "$scratch"
mount -t ext4 -o rw,nosuid,nodev "$scratch" /run/csweet
chmod 0711 /run/csweet
install -d -o csweet-workload -g csweet-workload -m 0700 /run/csweet/workload
exec /usr/lib/csweet/guest/CSweet.AgentRuntime.Guest
SCRIPT
chmod 0755 /usr/lib/csweet/prepare-runtime.sh

cat >/etc/systemd/system/csweet-vsock.service <<'UNIT'
[Unit]
Description=Load the Firecracker guest vsock transport
After=systemd-modules-load.service
Before=csweet-agent-guest.service

[Service]
Type=oneshot
ExecStart=/bin/sh -c '/sbin/modprobe vsock 2>/dev/null || true; /sbin/modprobe vmw_vsock_virtio_transport 2>/dev/null || true'
RemainAfterExit=yes
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

cat >/etc/systemd/system/csweet-agent-guest.service <<'UNIT'
[Unit]
Description=C-Sweet isolated Firecracker guest broker
After=local-fs.target csweet-vsock.service
Requires=csweet-vsock.service

[Service]
Type=simple
Environment=CSWEET_GUEST_BROKER_TRANSPORT=firecracker-vsock
Environment=CSWEET_GUEST_VSOCK_PORT=5000
Environment=CSWEET_GUEST_ARTIFACT_DEVICE=/dev/vdc
ExecStart=/usr/lib/csweet/prepare-runtime.sh
StandardOutput=journal+console
StandardError=journal+console
Restart=no
NoNewPrivileges=false
PrivateTmp=true
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
RestrictRealtime=true
LockPersonality=true

[Install]
WantedBy=multi-user.target
UNIT

systemctl enable csweet-vsock.service csweet-agent-guest.service
systemctl mask systemd-networkd.service systemd-networkd.socket systemd-resolved.service 2>/dev/null || true
cat >/etc/fstab <<'FSTAB'
/dev/vda / ext4 ro,nosuid,nodev 0 1
tmpfs /tmp tmpfs rw,nosuid,nodev,mode=1777 0 0
FSTAB
rm -f /etc/machine-id
touch /etc/machine-id
apt-get clean
rm -rf /var/lib/apt/lists/* /tmp/CSweet.AgentRuntime.Guest /tmp/CSweet.AgentRuntime.Builder
sync
