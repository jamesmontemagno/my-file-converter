#!/usr/bin/env python3
"""Read-only App Store diagnostics and narrowly scoped internal TestFlight delivery."""

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

from store_release import BUNDLE_ID, private_write, require, versions


def base64url(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def es256_signature(der):
    def item(offset, tag):
        require(offset + 2 <= len(der) and der[offset] == tag, "Invalid ECDSA signature.")
        size = der[offset + 1]
        require(size < 128 and offset + 2 + size <= len(der), "Invalid ECDSA signature length.")
        return der[offset + 2:offset + 2 + size], offset + 2 + size
    sequence, end = item(0, 0x30)
    require(end == len(der), "Unexpected trailing ECDSA signature data.")
    first, offset = item(2, 0x02)
    second, end = item(offset, 0x02)
    require(end == len(der) and sequence, "Invalid ECDSA signature sequence.")
    values = [value.lstrip(b"\0") for value in (first, second)]
    require(all(0 < len(value) <= 32 for value in values), "Invalid ES256 signature size.")
    return b"".join(value.rjust(32, b"\0") for value in values)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        raise ValueError("Unexpected redirect from the authenticated Apple API.")


class Client:
    origin = "https://api.appstoreconnect.apple.com"

    def token(self):
        key_id = os.environ["APPLE_REFRACTORED_KEY_ID"]
        issuer = os.environ["APPLE_REFRACTORED_ISSUER_ID"]
        key = os.environ["APPLE_REFRACTORED_P8_KEY"]
        require(key_id and issuer and "PRIVATE KEY" in key, "Missing App Store Connect API configuration.")
        now = int(time.time())
        header = base64url(json.dumps({"alg": "ES256", "kid": key_id, "typ": "JWT"}).encode())
        payload = base64url(json.dumps({"iss": issuer, "iat": now, "exp": now + 900,
                                       "aud": "appstoreconnect-v1"}).encode())
        message = (header + "." + payload).encode()
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "key.p8"
            private_write(path, key.encode())
            result = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(path)],
                                    input=message, capture_output=True)
            require(result.returncode == 0, "Could not sign App Store authentication. Check the P8 key.")
        return message.decode() + "." + base64url(es256_signature(result.stdout))

    def request(self, path, method="GET", data=None):
        url = self.origin + path if path.startswith("/v1/") else path
        parsed = urllib.parse.urlsplit(url)
        require(parsed.scheme == "https" and parsed.netloc == "api.appstoreconnect.apple.com"
                and parsed.path.startswith("/v1/"), "Refusing to send credentials outside the Apple API.")
        require(method in {"GET", "POST"}, "Unsupported TestFlight API operation.")
        if method == "POST":
            require(parsed.path.startswith("/v1/betaGroups/") and parsed.path.endswith("/relationships/builds"),
                    "Only internal group/build assignment is permitted.")
        request = urllib.request.Request(url, method=method,
            data=json.dumps(data).encode() if data is not None else None,
            headers={"Authorization": "Bearer " + self.token(), "Content-Type": "application/json"})
        try:
            with urllib.request.build_opener(NoRedirect()).open(request, timeout=60) as response:
                body = response.read()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as error:
            raise ValueError(f"App Store API returned HTTP {error.code}. Check app access, API role, build state and internal group configuration.") from None

    def pages(self, path, **query):
        path += "?" + urllib.parse.urlencode({"limit": 200, **query})
        for _ in range(100):
            response = self.request(path)
            yield response
            path = response.get("links", {}).get("next")
            if not path:
                return
        raise ValueError("Apple pagination exceeded its safety limit.")

    def resolve(self, group_name):
        apps = [item for page in self.pages("/v1/apps", **{"filter[bundleId]": BUNDLE_ID}) for item in page["data"]]
        require(len(apps) == 1, "Expected exactly one accessible LocalMorph App Store Connect app record.")
        app = apps[0]
        groups = [group for page in self.pages("/v1/betaGroups", **{"filter[app]": app["id"]})
                  for group in page["data"] if group["attributes"].get("isInternalGroup") is True]
        if group_name:
            groups = [group for group in groups if group["attributes"]["name"] == group_name]
        require(len(groups) == 1, "Choose an exact internal TestFlight group name, or create one internal group in App Store Connect.")
        return app, groups[0]

    def builds(self, app_id, version, build):
        matches = []
        for page in self.pages("/v1/builds", **{"filter[app]": app_id, "filter[version]": build,
                                               "include": "preReleaseVersion"}):
            releases = {item["id"]: item for item in page.get("included", []) if item["type"] == "preReleaseVersions"}
            for item in page["data"]:
                relationship = item["relationships"]["preReleaseVersion"]["data"]
                release = releases[relationship["id"]]["attributes"]
                if release["version"] == version and release["platform"] == "MAC_OS":
                    require(item["attributes"]["version"] == build, "Apple returned a different build number.")
                    matches.append(item)
        require(len(matches) <= 1, "Ambiguous processed Mac build; refusing to select one.")
        return matches

    def assigned(self, group_id, build_id):
        group_id = urllib.parse.quote(group_id, safe="")
        return any(item["id"] == build_id for page in self.pages(f"/v1/betaGroups/{group_id}/relationships/builds")
                   for item in page["data"])

    def wait_and_assign(self, app, group, version, build, timeout):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            builds = self.builds(app["id"], version, build)
            if builds:
                selected = builds[0]
                state = selected["attributes"]["processingState"]
                require(state not in {"FAILED", "INVALID"}, f"Apple rejected build processing: {state}. Inspect App Store Connect.")
                if state == "VALID":
                    break
            time.sleep(30)
        else:
            raise ValueError("Timed out waiting for Apple processing. The upload may still exist; diagnose before retrying, and do not blindly reuse its build number.")
        identifier = selected["id"]
        if not self.assigned(group["id"], identifier):
            if group["attributes"].get("hasAccessToAllBuilds"):
                # Apple manages membership for automatic all-builds groups.
                for _ in range(6):
                    time.sleep(20)
                    if self.assigned(group["id"], identifier):
                        break
            else:
                self.request(f"/v1/betaGroups/{urllib.parse.quote(group['id'], safe='')}/relationships/builds",
                             method="POST", data={"data": [{"type": "builds", "id": identifier}]})
        require(self.assigned(group["id"], identifier), "Build processed, but internal group membership is not confirmed.")
        return {"app_id": app["id"], "build_id": identifier, "version": version, "build": build,
                "group_id": group["id"], "group_name": group["attributes"]["name"],
                "processing_state": "VALID", "group_assignment": "confirmed",
                "review_submitted": False, "publicly_released": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["diagnose", "preflight", "deliver"])
    parser.add_argument("--version", required=True)
    parser.add_argument("--build", required=True)
    parser.add_argument("--group", default="")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--timeout", type=int, default=1200)
    args = parser.parse_args()
    versions(args.version, args.build)
    require(0 < args.timeout <= 1200, "Processing timeout must be between 1 and 1200 seconds.")
    client = Client()
    app, group = client.resolve(args.group)
    existing = client.builds(app["id"], args.version, args.build)
    if args.mode == "preflight":
        require(not existing, "This Mac version/build already exists in App Store Connect. Use a new build_number; uploads are not repeated automatically.")
    result = {"app_id": app["id"], "bundle_id": BUNDLE_ID, "group_id": group["id"],
              "group_name": group["attributes"]["name"], "existing_builds": existing,
              "mode": args.mode, "store_mutations": False}
    if args.mode == "deliver":
        result = client.wait_and_assign(app, group, args.version, args.build, args.timeout)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError) as error:
        print(f"TestFlight failed: {error}", file=sys.stderr)
        sys.exit(1)
