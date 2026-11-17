import argparse
import json
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen
import sys


def decode_body(raw):
    text = raw.decode("utf-8")
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def response_for(base_url, request_data):
    method = request_data["method"].upper()
    path = request_data["path"]
    url = urljoin(base_url.rstrip("/") + "/", path.lstrip("/"))
    body = request_data.get("body")
    encoded_body = None if body is None else json.dumps(body).encode("utf-8")
    request = Request(
        url,
        data=encoded_body,
        method=method,
        headers={"Accept": "application/json", "Content-Type": "application/json"},
    )
    try:
        with urlopen(request) as response:
            status = response.status
            language = response.headers.get("Content-Language")
            response_body = decode_body(response.read())
    except HTTPError as error:
        status = error.code
        language = error.headers.get("Content-Language")
        response_body = decode_body(error.read())
    return {"status": status, "content_language": language, "body": response_body}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    args = parser.parse_args()
    try:
        request_data = json.load(sys.stdin)
        result = response_for(args.base_url, request_data)
    except (KeyError, json.JSONDecodeError, URLError, OSError, ValueError) as error:
        print(f"API corpus request failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
