#!/bin/bash
set -euo pipefail

usage() {
  echo "usage: $0 PAYLOAD_ROOT OUTPUT_ROOT VERSION [--format deb|rpm|all] [--deb-signing-key KEY] [--rpm-signing-key KEY]" >&2
  exit 2
}

[ "$#" -ge 3 ] || usage
payload_root=$(realpath "$1")
output_root=$(realpath -m "$2")
version=$3
shift 3
format=all
deb_signing_key=
rpm_signing_key=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --format) [ "$#" -ge 2 ] || usage; format=$2; shift 2 ;;
    --deb-signing-key) [ "$#" -ge 2 ] || usage; deb_signing_key=$2; shift 2 ;;
    --rpm-signing-key) [ "$#" -ge 2 ] || usage; rpm_signing_key=$2; shift 2 ;;
    *) usage ;;
  esac
done
case "$format" in deb|rpm|all) ;; *) usage ;; esac
case "$version" in
  ''|*[!0-9.]*) echo "VERSION must contain only numeric dot-separated components." >&2; exit 2 ;;
esac
printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || {
  echo "VERSION must use the form MAJOR.MINOR.PATCH." >&2; exit 2;
}

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
for required in runtime-manifest.json install-execution-node.sh uninstall-execution-node.sh \
  csweet-runtime-host.service csweet-execution-node.service CSweet.RuntimeHost CSweet.ExecutionNode; do
  [ -e "$payload_root/$required" ] || { echo "Payload is missing $required." >&2; exit 2; }
done
[ -f "$script_root/configure-execution-node.sh" ] || {
  echo "configure-execution-node.sh is missing." >&2; exit 2;
}
if find "$payload_root" -type l -print -quit | grep -q .; then
  echo "Execution packages may not contain symbolic links." >&2
  exit 2
fi

case "$(uname -m)" in
  x86_64) deb_arch=amd64; rpm_arch=x86_64 ;;
  aarch64|arm64) deb_arch=arm64; rpm_arch=aarch64 ;;
  *) echo "Only x86-64 and arm64 package hosts are supported." >&2; exit 2 ;;
esac

mkdir -p "$output_root"
build_root=$(mktemp -d)
trap 'rm -rf -- "$build_root"' EXIT

stage_package_tree() {
  destination=$1
  mkdir -p "$destination/usr/lib/csweet/execution-installer" "$destination/usr/sbin"
  cp -a "$payload_root/." "$destination/usr/lib/csweet/execution-installer/"
  install -m 0755 "$script_root/configure-execution-node.sh" \
    "$destination/usr/sbin/csweet-configure-execution-node"
  install -m 0755 "$script_root/uninstall-execution-node.sh" \
    "$destination/usr/sbin/csweet-uninstall-execution-node"
}

build_deb() {
  [ -n "$deb_signing_key" ] || { echo "--deb-signing-key is required for DEB output." >&2; exit 2; }
  for command_name in dpkg-deb dpkg-sig; do
    command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required for DEB output." >&2; exit 2; }
  done
  deb_root="$build_root/deb"
  stage_package_tree "$deb_root"
  mkdir -p "$deb_root/DEBIAN"
  cat > "$deb_root/DEBIAN/control" <<EOF
Package: csweet-execution-node
Version: $version
Section: admin
Priority: optional
Architecture: $deb_arch
Maintainer: C-Sweet Release Engineering
Depends: systemd
Description: C-Sweet ExecutionNode and RuntimeHost enrollment payload
 Installs the signed, non-secret runtime payload. Run
 csweet-configure-execution-node separately to enroll this machine.
EOF
  cat > "$deb_root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
echo "C-Sweet payload installed. Enroll with: sudo csweet-configure-execution-node https://CONTROL-PLANE"
EOF
  cat > "$deb_root/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "${1:-}" = remove ] && { [ -e /opt/csweet/execution/CSweet.ExecutionNode ] || [ -e /etc/systemd/system/csweet-execution-node.service ]; }; then
  /usr/sbin/csweet-uninstall-execution-node
fi
EOF
  chmod 0755 "$deb_root/DEBIAN/postinst" "$deb_root/DEBIAN/prerm"
  deb_path="$output_root/csweet-execution-node_${version}_${deb_arch}.deb"
  [ ! -e "$deb_path" ] || { echo "Output already exists: $deb_path" >&2; exit 2; }
  dpkg-deb --root-owner-group --build "$deb_root" "$deb_path"
  dpkg-sig --sign builder -k "$deb_signing_key" "$deb_path"
  dpkg-deb --info "$deb_path" >/dev/null
  dpkg-sig --verify "$deb_path"
  echo "Created $deb_path"
}

build_rpm() {
  [ -n "$rpm_signing_key" ] || { echo "--rpm-signing-key is required for RPM output." >&2; exit 2; }
  for command_name in rpmbuild rpmsign rpm; do
    command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required for RPM output." >&2; exit 2; }
  done
  rpm_root="$build_root/rpmbuild"
  source_name="csweet-execution-node-$version"
  mkdir -p "$rpm_root/BUILD" "$rpm_root/BUILDROOT" "$rpm_root/RPMS" "$rpm_root/SOURCES" "$rpm_root/SPECS" "$rpm_root/SRPMS"
  mkdir -p "$build_root/source/$source_name"
  stage_package_tree "$build_root/source/$source_name"
  tar -C "$build_root/source" -czf "$rpm_root/SOURCES/$source_name.tar.gz" "$source_name"
  cat > "$rpm_root/SPECS/csweet-execution-node.spec" <<EOF
Name: csweet-execution-node
Version: $version
Release: 1%{?dist}
Summary: C-Sweet ExecutionNode and RuntimeHost enrollment payload
License: Proprietary
BuildArch: $rpm_arch
Source0: %{name}-%{version}.tar.gz
Requires: systemd

%description
Installs the signed, non-secret C-Sweet runtime payload. Enrollment is a
separate protected operation.

%prep
%setup -q

%build

%install
mkdir -p %{buildroot}
cp -a usr %{buildroot}/

%post
echo "C-Sweet payload installed. Enroll with: sudo csweet-configure-execution-node https://CONTROL-PLANE"

%preun
if [ \$1 -eq 0 ] && { [ -e /opt/csweet/execution/CSweet.ExecutionNode ] || [ -e /etc/systemd/system/csweet-execution-node.service ]; }; then
  /usr/sbin/csweet-uninstall-execution-node
fi

%files
%defattr(-,root,root,-)
/usr/lib/csweet/execution-installer
/usr/sbin/csweet-configure-execution-node
/usr/sbin/csweet-uninstall-execution-node
EOF
  rpmbuild --define "_topdir $rpm_root" -bb "$rpm_root/SPECS/csweet-execution-node.spec"
  rpm_path=$(find "$rpm_root/RPMS" -type f -name '*.rpm' -print -quit)
  [ -n "$rpm_path" ] || { echo "rpmbuild did not produce an RPM." >&2; exit 2; }
  output_path="$output_root/$(basename "$rpm_path")"
  [ ! -e "$output_path" ] || { echo "Output already exists: $output_path" >&2; exit 2; }
  cp "$rpm_path" "$output_path"
  rpmsign --define "_gpg_name $rpm_signing_key" --addsign "$output_path"
  rpm --checksig "$output_path" | grep -q 'digests signatures OK$' || {
    echo "RPM signature validation failed." >&2; exit 2;
  }
  echo "Created $output_path"
}

case "$format" in
  deb) build_deb ;;
  rpm) build_rpm ;;
  all) build_deb; build_rpm ;;
esac
