#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then echo "Run this installer as root." >&2; exit 1; fi
if [ "$#" -lt 2 ]; then echo "usage: $0 PACKAGE_ROOT https://control-plane [--enrollment-token-file PATH] [--result-job-id ID]" >&2; exit 2; fi
package_root=$(readlink -f "$1")
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
  install -d -o root -g root -m 0755 /var/lib/csweet/setup
  result_file=/var/lib/csweet/setup/local-provisioning-$result_job_id.result
fi
write_result() {
  if [ -n "$result_file" ]; then printf '%s\n' "$1" > "$result_file"; chmod 0644 "$result_file"; fi
}
trap 'write_result failed' 0
case "$control_plane" in https://*) ;; *) echo "Control-plane URL must use HTTPS." >&2; exit 2;; esac
if [ ! -x "$package_root/CSweet.RuntimeHost" ] || [ ! -x "$package_root/CSweet.ExecutionNode" ] ||
   [ ! -x "$package_root/CSweet.AgentRuntime.Firecracker.Helper" ] ||
   [ ! -x "$package_root/firecracker/firecracker" ] || [ ! -x "$package_root/firecracker/jailer" ] ||
   [ ! -f "$package_root/firecracker/vmlinux" ] || [ ! -f "$package_root/firecracker/initrd.img" ] ||
   [ ! -f "$package_root/runtime-manifest.json" ]; then
  echo "The signed RuntimeHost/ExecutionNode/Firecracker package is incomplete." >&2; exit 2
fi
if [ ! -r /sys/fs/cgroup/cgroup.controllers ] || [ ! -r /dev/kvm ] || [ ! -w /dev/kvm ]; then
  echo "Firecracker requires cgroup v2 and read/write access to /dev/kvm." >&2; exit 2
fi
if find "$package_root" -type l -print -quit | grep -q .; then
  echo "Execution packages may not contain symbolic links." >&2; exit 2
fi
if [ -f /etc/systemd/system/csweet-execution-node.service ] || [ -e /opt/csweet/execution/CSweet.ExecutionNode ]; then
  maintenance=/var/lib/csweet/execution-node/maintenance
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
  systemctl stop csweet-execution-node.service 2>/dev/null || true
  systemctl stop csweet-runtime-host.service 2>/dev/null || true
fi
if [ -n "$token_file" ]; then
  [ -f "$token_file" ] && [ ! -L "$token_file" ] || { echo "The protected enrollment input is invalid." >&2; exit 2; }
  token=$(tr -d '\r\n' < "$token_file")
  rm -f -- "$token_file"
elif [ -t 0 ]; then printf 'Enrollment token: ' >&2; stty -echo; IFS= read -r token; stty echo; printf '\n' >&2
else IFS= read -r token; fi
if [ "${#token}" -lt 32 ] || [ "${#token}" -gt 256 ]; then echo "Invalid enrollment token." >&2; exit 2; fi

install -d -m 0755 /opt/csweet/execution /etc/csweet /var/lib/csweet/execution-node /var/lib/csweet/runtime-host /var/lib/csweet/runtime-host/firecracker
chown root:root /var/lib/csweet/runtime-host/firecracker
chmod 0700 /var/lib/csweet/runtime-host/firecracker
cp -R "$package_root/." /opt/csweet/execution/
chown -R root:root /opt/csweet/execution
chmod 0755 /opt/csweet/execution/CSweet.RuntimeHost /opt/csweet/execution/CSweet.ExecutionNode /opt/csweet/execution/CSweet.AgentRuntime.Firecracker.Helper
chmod 0755 /opt/csweet/execution/firecracker/firecracker /opt/csweet/execution/firecracker/jailer
chmod 0644 /opt/csweet/execution/firecracker/vmlinux
chmod 0644 /opt/csweet/execution/firecracker/initrd.img
chmod 0644 /opt/csweet/execution/runtime-manifest.json
id csweet-node >/dev/null 2>&1 || useradd --system --home /var/lib/csweet/execution-node --shell /usr/sbin/nologin csweet-node
id csweet-vm >/dev/null 2>&1 || useradd --system --home /nonexistent --shell /usr/sbin/nologin csweet-vm
getent group csweet-runtime >/dev/null 2>&1 || groupadd --system csweet-runtime
usermod -a -G csweet-runtime csweet-node
vm_uid=$(id -u csweet-vm)
vm_gid=$(id -g csweet-vm)
install -d -o csweet-node -g csweet-runtime -m 0770 /var/lib/csweet/artifact-media
if [ ! -f /var/lib/csweet/runtime-host/runtime-host.key ]; then
  umask 007
  dd if=/dev/urandom bs=32 count=1 2>/dev/null | base64 > /var/lib/csweet/runtime-host/runtime-host.key
fi
chown root:csweet-runtime /var/lib/csweet/runtime-host/runtime-host.key
chmod 0640 /var/lib/csweet/runtime-host/runtime-host.key
install -o csweet-node -g csweet-node -m 0600 /dev/null /var/lib/csweet/execution-node/enrollment.secret
printf '%s' "$token" > /var/lib/csweet/execution-node/enrollment.secret
unset token
cat > /etc/csweet/execution-node.env <<EOF
CSweet__ExecutionNode__ControlPlaneUrl=$control_plane
CSweet__ExecutionNode__StateDirectory=/var/lib/csweet/execution-node
CSweet__ExecutionNode__ArtifactCacheDirectory=/var/lib/csweet/execution-node/artifact-cache
CSweet__ExecutionNode__ArtifactMediaDirectory=/var/lib/csweet/artifact-media
CSweet__ExecutionNode__EnrollmentTokenFilePath=/var/lib/csweet/execution-node/enrollment.secret
CSweet__AgentRuntime__RuntimeHost__UnixSocketPath=/run/csweet/runtime-host-v1.sock
CSweet__AgentRuntime__HostAuthentication__SharedKeyFilePath=/var/lib/csweet/runtime-host/runtime-host.key
EOF
chmod 0600 /etc/csweet/execution-node.env
cat > /etc/csweet/runtime-host.env <<EOF
CSWEET_FIRECRACKER_DATA_ROOT=/var/lib/csweet/runtime-host/firecracker
CSWEET_FIRECRACKER_PACKAGE_ROOT=/opt/csweet/execution/firecracker
CSWEET_FIRECRACKER_WORKLOAD_UID=$vm_uid
CSWEET_FIRECRACKER_WORKLOAD_GID=$vm_gid
CSWEET_FIRECRACKER_GUEST_VSOCK_PORT=5000
CSWEET_ARTIFACT_MEDIA_ROOT=/var/lib/csweet/artifact-media
EOF
chmod 0600 /etc/csweet/runtime-host.env
install -m 0644 "$package_root/csweet-runtime-host.service" /etc/systemd/system/csweet-runtime-host.service
install -m 0644 "$package_root/csweet-execution-node.service" /etc/systemd/system/csweet-execution-node.service
systemctl daemon-reload
systemctl enable --now csweet-runtime-host.service csweet-execution-node.service
write_result completed
trap - 0
