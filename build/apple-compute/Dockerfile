FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS publish
WORKDIR /src
COPY Directory.* ./
COPY src/CSweet.Compute.Guest/ src/CSweet.Compute.Guest/
RUN dotnet publish src/CSweet.Compute.Guest -c Release --use-current-runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o /payload

FROM ubuntu:24.04 AS guest
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends systemd-sysv dbus udev kmod linux-image-virtual initramfs-tools e2fsprogs docker.io python3 busybox-static libicu74 libssl3t64 && rm -rf /var/lib/apt/lists/*
COPY --from=publish /payload/CSweet.Compute.Guest /opt/csweet/compute-guest/CSweet.Compute.Guest
COPY build/apple-compute/csweet-compute-guest.service /etc/systemd/system/
RUN mkdir -p /var/lib/csweet-compute/work /etc/docker && \
    echo '{"bridge":"none","iptables":false,"ip6tables":false}' >/etc/docker/daemon.json && \
    echo vmw_vsock_virtio_transport >/etc/modules-load.d/csweet-compute.conf && \
    systemctl enable dbus.socket docker csweet-compute-guest && \
    echo '/dev/vda / ext4 defaults 0 1' >/etc/fstab && truncate -s0 /etc/machine-id

FROM ubuntu:24.04 AS disk
RUN apt-get update && apt-get install -y --no-install-recommends e2fsprogs gzip && rm -rf /var/lib/apt/lists/*
COPY --from=guest / /rootfs/
ARG TARGETARCH
RUN set -eu; mkdir /out; kernel=$(ls /rootfs/boot/vmlinuz-*); version=${kernel##*/vmlinuz-}; \
    if [ "$TARGETARCH" = arm64 ] && gzip -t "$kernel" 2>/dev/null; then gzip -dc "$kernel" >/out/vmlinux; else cp "$kernel" /out/vmlinux; fi; \
    cp "/rootfs/boot/initrd.img-$version" /out/initrd.img; rm -rf /rootfs/boot/*; \
    size=$(du -sm /rootfs | cut -f1); truncate -s "$((size + 256))M" /out/compute.img; \
    mkfs.ext4 -q -F -L CSWEET_COMPUTE -d /rootfs /out/compute.img; e2fsck -fn /out/compute.img; chmod 0444 /out/*
FROM scratch
COPY --from=disk /out/ /
