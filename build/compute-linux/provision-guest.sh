#!/usr/bin/env bash
set -euo pipefail
payload=/tmp/csweet-image-payload
bash "$payload/Install-ComputeGuestLinux.sh" "$payload/guest" --image-build
test -x /opt/csweet/compute-guest/CSweet.Compute.Guest
test -x /usr/bin/python3
test -x /usr/bin/docker
systemctl enable --now docker
# Operator-prepared image content; runtime VMs still have no network adapter.
# Immutable official amd64 bases allow initial app builds without network grants.
docker pull python@sha256:2fe5997d249a808b8eeea52c58a1dbffbba28754dc11699ef5c029f2d818ce79
docker tag python@sha256:2fe5997d249a808b8eeea52c58a1dbffbba28754dc11699ef5c029f2d818ce79 csweet/python:3.12
docker pull node@sha256:4d676821dff059fd00d277ee4261ef34ea712317fed0737c03941481b5760c96
docker tag node@sha256:4d676821dff059fd00d277ee4261ef34ea712317fed0737c03941481b5760c96 csweet/node:22
docker run --rm --network=none csweet/python:3.12 python --version
docker run --rm --network=none csweet/node:22 node --version
install -d /etc/csweet
printf '%s\n' 'docker-apps-v1' > /etc/csweet/compute-profile
