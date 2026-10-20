#!/bin/sh
set -eu

# Keep the checked-in configuration read-only and never use upstream's default
# secret. A supplied secret is supported; otherwise sessions reset on restart.
if [ -z "${SEARXNG_SECRET:-}" ]; then
    SEARXNG_SECRET="$(/usr/local/searxng/.venv/bin/python -c 'import secrets; print(secrets.token_hex(32))')"
    export SEARXNG_SECRET
fi

exec /usr/local/searxng/entrypoint.sh
