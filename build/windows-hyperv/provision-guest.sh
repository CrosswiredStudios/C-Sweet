#!/usr/bin/env bash
set -euo pipefail

guest_source=/tmp/CSweet.AgentRuntime.Guest
guest_root=/usr/lib/csweet/guest
if [[ ! -f "$guest_source" ]]; then
  echo 'Published C-Sweet guest executable is missing.' >&2
  exit 2
fi

install -d -m 0755 "$guest_root"
install -o root -g root -m 0755 "$guest_source" "$guest_root/CSweet.AgentRuntime.Guest"

cat >/usr/lib/csweet/prepare-runtime.sh <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail

install -d -o root -g root -m 0700 /run/csweet
root_source=$(findmnt -n -o SOURCE /)
root_parent=$(lsblk -no PKNAME "$root_source" 2>/dev/null | head -n1 || true)
if [[ -n "$root_parent" ]]; then
  root_disk="/dev/$root_parent"
else
  root_disk="$root_source"
fi

scratch=''
for attempt in $(seq 1 30); do
  mapfile -t candidates < <(lsblk -dpno NAME,TYPE | awk '$2 == "disk" { print $1 }' | grep -Fvx "$root_disk" || true)
  if [[ ${#candidates[@]} -eq 1 ]]; then
    scratch=${candidates[0]}
    break
  fi
  sleep 1
done
if [[ -z "$scratch" ]]; then
  echo 'The disposable scratch disk could not be identified unambiguously.' >&2
  exit 3
fi
if findmnt -rn -S "$scratch" >/dev/null; then
  echo 'The disposable scratch disk is already mounted unexpectedly.' >&2
  exit 4
fi

wipefs --all --force "$scratch"
mkfs.ext4 -F -L CSWEET_SCRATCH "$scratch"
mount -t ext4 -o rw,nosuid,nodev "$scratch" /run/csweet
chmod 0700 /run/csweet
exec /usr/lib/csweet/guest/CSweet.AgentRuntime.Guest
SCRIPT
chmod 0755 /usr/lib/csweet/prepare-runtime.sh

cat >/usr/lib/csweet/first-runtime-boot.sh <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
systemctl disable ssh.service ssh.socket >/dev/null 2>&1 || true
systemctl mask ssh.service ssh.socket >/dev/null 2>&1 || true
rm -rf /home/csweet-image/.ssh
rm -f /etc/sudoers.d/90-csweet-image
passwd -l csweet-image >/dev/null 2>&1 || true
touch /var/lib/csweet-runtime-hardened
SCRIPT
chmod 0755 /usr/lib/csweet/first-runtime-boot.sh

cat >/etc/systemd/system/csweet-first-runtime-boot.service <<'UNIT'
[Unit]
Description=Remove C-Sweet image-build access before accepting an agent workload
Before=csweet-agent-guest.service
ConditionPathExists=!/var/lib/csweet-runtime-hardened

[Service]
Type=oneshot
ExecStart=/usr/lib/csweet/first-runtime-boot.sh

[Install]
WantedBy=multi-user.target
UNIT

cat >/etc/systemd/system/csweet-agent-guest.service <<'UNIT'
[Unit]
Description=C-Sweet isolated guest broker
After=local-fs.target csweet-first-runtime-boot.service csweet-hv-sock.service
Requires=csweet-first-runtime-boot.service csweet-hv-sock.service

[Service]
Type=simple
Environment=CSWEET_GUEST_BROKER_TRANSPORT=hyperv-vsock
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

cat >/etc/systemd/system/csweet-hv-sock.service <<'UNIT'
[Unit]
Description=Load the Hyper-V VSOCK transport before broker sandboxing
After=systemd-modules-load.service
Before=csweet-agent-guest.service

[Service]
Type=oneshot
ExecStart=/sbin/modprobe hv_sock
RemainAfterExit=yes
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

printf 'hv_sock\n' >/etc/modules-load.d/csweet-hv-sock.conf
systemctl enable csweet-first-runtime-boot.service csweet-hv-sock.service csweet-agent-guest.service
touch /etc/cloud/cloud-init.disabled
rm -rf /var/lib/cloud/instances/*
apt-get clean
rm -rf /var/lib/apt/lists/* /tmp/CSweet.AgentRuntime.Guest
sync
