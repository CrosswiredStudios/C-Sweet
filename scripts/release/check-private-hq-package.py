"""Require a pre-existing private GHCR package and a denied anonymous catalog read."""
import json
import os
import re
import sys
from urllib.error import HTTPError
from urllib.request import HTTPRedirectHandler, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def request(url, token=None):
    headers = {"User-Agent": "linux-headquarters-release"}
    if token:
        headers["Authorization"] = "Bearer " + token
    try:
        with build_opener(NoRedirect()).open(Request(url, headers=headers), timeout=20) as response:
            return response.status, json.load(response)
    except HTTPError as error:
        return error.code, {}


def require_private(image):
    match = re.fullmatch(r"ghcr\.io/([a-z0-9-]+)/([a-z0-9-]+)", image)
    if not match:
        sys.exit("Expected a GHCR package reference")
    owner, name = match.groups()
    token = os.environ["GH_TOKEN"]
    status, account = request(f"https://api.github.com/users/{owner}", token)
    if status != 200 or account.get("type") not in ("User", "Organization"):
        sys.exit("Could not verify package owner")
    scope = "orgs" if account["type"] == "Organization" else "users"
    status, package = request(f"https://api.github.com/{scope}/{owner}/packages/container/{name}", token)
    if (status != 200 or package.get("visibility") != "private" or package.get("name") != name
            or package.get("owner", {}).get("login", "").lower() != owner):
        sys.exit(f"Private package metadata required for {image} (HTTP {status}); upload blocked")

    status, result = request(f"https://ghcr.io/token?service=ghcr.io&scope=repository:{owner}/{name}:pull")
    if status == 200 and result.get("token"):
        status, _ = request(f"https://ghcr.io/v2/{owner}/{name}/tags/list?n=1", result["token"])
    if status not in (401, 403):
        sys.exit(f"Anonymous pull denial required for {image} (HTTP {status}); upload blocked")
    print(f"Verified {image} is private and anonymous access is denied")


if __name__ == "__main__":
    require_private(sys.argv[1])
