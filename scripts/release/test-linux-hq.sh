#!/usr/bin/env bash
# Smoke-test the published binaries, database migrations, frontend and purchaser reservation.
set -euo pipefail
image=${1:?Supply the local Headquarters image}
suffix=$(cat /proc/sys/kernel/random/uuid)
network="csweet-smoke-$suffix"
db="hq-db-$suffix"
api="hq-api-$suffix"
password=$(openssl rand -hex 24)
key=$(openssl rand -hex 32)
cleanup() {
  docker rm -fv "$api" "$db" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "$network" >/dev/null
docker run -d --name "$db" --network "$network" --network-alias postgres -e POSTGRES_DB=csweet -e POSTGRES_PASSWORD="$password" postgres:17-alpine >/dev/null
for _ in {1..60}; do docker exec "$db" pg_isready -U postgres >/dev/null && break; sleep 1; done
shared=(--network "$network" -e "ConnectionStrings__csweet=Host=postgres;Database=csweet;Username=postgres;Password=$password")
docker run --rm "${shared[@]}" -w /srv/CSweet.Migrator "$image" /srv/CSweet.Migrator/CSweet.Migrator.dll
docker run -d --name "$api" "${shared[@]}" -p 127.0.0.1::8181 -e ASPNETCORE_URLS=http://+:8181 \
  -e Installation__ServePackagedWeb=true -e Installation__LinkingEnabled=true \
  -e Installation__ReservedOwnerSubject=buyer -e Installation__DeploymentId=test -e Installation__Hostname=hq-test.csweet.app \
  -e Installation__Release=fixture -e Installation__ReadinessKey="$key" -w /srv/CSweet.Api "$image" /srv/CSweet.Api/CSweet.Api.dll >/dev/null
origin="http://$(docker port "$api" 8181/tcp)"
for _ in {1..90}; do curl -fsS "$origin/health" >/dev/null 2>&1 && break; sleep 1; done
if ! curl -fsS "$origin/health"; then docker logs --tail 40 "$api"; exit 1; fi
# The application resolves its media storage under LocalApplicationData at runtime.
docker exec "$api" sh -ec 'test -d "$HOME/.local/share" && test -w "$HOME/.local/share"'
# Follow the links a browser loads, including the fingerprinted component CSS.
python3 - "$origin/" <<'PYTHON'
from html.parser import HTMLParser
import re
import sys
from urllib.parse import urljoin
from urllib.request import urlopen

class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.styles = []
        self.scripts = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "link" and "stylesheet" in attrs.get("rel", "").split():
            self.styles.append(attrs["href"])
        if tag == "script":
            self.scripts.append(attrs.get("src", ""))

def read_css(url):
    with urlopen(url, timeout=10) as response:
        if response.headers.get_content_type() != "text/css":
            raise ValueError(f"Expected CSS at {url}")
        return response.read().decode()

origin = sys.argv[1]
page = Page()
with urlopen(origin, timeout=10) as response:
    page.feed(response.read().decode())
assert any("blazor.webassembly" in src for src in page.scripts), "Blazor loader missing"
assert "CSweet.App.styles.css" in page.styles, "Application stylesheet missing"
for href in page.styles:
    url = urljoin(origin, href)
    css = read_css(url)
    if href == "CSweet.App.styles.css":
        imports = re.findall(r"""@import\s+['"]([^'"]+)['"]""", css)
        assert any("CSweet.UI" in path for path in imports), "Component stylesheet import missing"
        for path in imports:
            assert read_css(urljoin(url, path)).strip(), f"Empty stylesheet: {path}"
PYTHON
nonce=$(openssl rand -hex 16)
proof=$(printf 'test\nbuyer\nhq-test.csweet.app\nfixture\n%s' "$nonce" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$key" | awk '{print $NF}')
curl -fsS "$origin/api/auth/hosted/readiness?nonce=$nonce" | jq -e --arg proof "$proof" '.proof==$proof'
status=$(curl -sS -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' \
  -d '{"displayName":"QA","email":"qa@example.test","password":"Test-example","confirmPassword":"Test-example"}' "$origin/api/auth/register")
test "$status" = 403
