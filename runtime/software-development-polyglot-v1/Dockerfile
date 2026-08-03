# Every stage image is supplied by the release pipeline as an immutable digest.
# See build.ps1; tags are deliberately not accepted by that script.
ARG NODE_IMAGE
ARG PYTHON_IMAGE
ARG POWERSHELL_IMAGE
ARG UV_IMAGE
ARG DOTNET_IMAGE

FROM ${NODE_IMAGE} AS node
FROM ${PYTHON_IMAGE} AS python
FROM ${POWERSHELL_IMAGE} AS powershell
FROM ${UV_IMAGE} AS uv
FROM ${DOTNET_IMAGE} AS runtime

USER root

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        bash \
        build-essential \
        ca-certificates \
        curl \
        git \
        git-lfs \
        openssh-client \
        pkg-config \
        ripgrep \
    && rm -rf /var/lib/apt/lists/* \
    && git lfs install --system

COPY --from=node /usr/local/ /usr/local/
COPY --from=python /usr/local/ /usr/local/
COPY --from=powershell /opt/microsoft/powershell/7/ /opt/microsoft/powershell/7/
COPY --from=uv /uv /uvx /usr/local/bin/

RUN ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm -f /usr/local/bin/yarn /usr/local/bin/yarnpkg /usr/local/bin/pnpm /usr/local/bin/pnpx \
    && corepack enable --install-directory /usr/local/bin \
    && (getent group 1654 >/dev/null || groupadd --gid 1654 csweet) \
    && (getent passwd 1654 >/dev/null || useradd --uid 1654 --gid 1654 --create-home --home-dir /home/csweet --shell /bin/bash csweet) \
    && mkdir -p /workspace /tmp/csweet-developer /home/csweet \
    && chown -R 1654:1654 /workspace /tmp/csweet-developer /home/csweet

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    COREPACK_ENABLE_DOWNLOAD_PROMPT=0 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HOME=/home/csweet

WORKDIR /workspace
USER 1654:1654

CMD ["bash", "-lc", "trap : TERM INT; sleep infinity & wait"]
