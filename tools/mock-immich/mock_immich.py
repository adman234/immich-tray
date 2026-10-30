"""
A small fake Immich server (v3.3 search API) for testing Immich Tray without a real server.

    python tools/mock-immich/mock_immich.py --media <folder of .jpg files> [--port 2283] [--page-size 1000]

API key: "test-key". Only the endpoints Immich Tray uses are implemented.
Add/remove assets while it runs with:  POST /mock/add?month=2026-10[&count=N][&name=X]  /  POST /mock/delete?id=<uuid>
"""
import argparse
import datetime as dt
import json
import os
import re
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

API_KEY = "test-key"
ME = "11111111-1111-4111-8111-111111111111"
PARTNER = "22222222-2222-4222-8222-222222222222"

lock = threading.Lock()
ASSETS = {}   # id -> dict
ALBUMS = {}   # id -> dict(name, owner, assets:set, updatedAt)
PEOPLE = {}   # id -> dict(name, assets:set)
MEDIA = []    # list of bytes
PAGE_SIZE = 1000


def uid(n):
    return f"{n:08x}-0000-4000-8000-{n:012x}"


def iso(d):
    return d.strftime("%Y-%m-%dT%H:%M:%S.000Z")


def add_asset(n, name, taken, *, kind="IMAGE", owner=ME, favorite=False, visibility="timeline",
              trashed=False, live_video=None, size_known=True, offset_hours=-5):
    media = MEDIA[n % len(MEDIA)] if kind == "IMAGE" else b"\x00\x00\x00\x18ftypmp42" + os.urandom(200_000)
    aid = uid(n)
    ASSETS[aid] = dict(
        id=aid, name=name, taken=taken, kind=kind, owner=owner, favorite=favorite, visibility=visibility,
        trashed=trashed, live=live_video, size_known=size_known, data=media, offset=offset_hours,
        updated=iso(dt.datetime.utcnow()),
    )
    return aid


def seed():
    base = dt.datetime(2026, 8, 1, 12, 0, 0)
    n = 1
    ids = []
    for i in range(24):
        taken = base + dt.timedelta(days=i * 4, hours=i)
        ids.append(add_asset(n, f"IMG_{1000 + i}.JPG", taken)); n += 1
    # duplicate names in the same month
    ids.append(add_asset(n, "IMG_DUP.JPG", dt.datetime(2026, 10, 3, 9)));  n += 1
    ids.append(add_asset(n, "IMG_DUP.JPG", dt.datetime(2026, 10, 4, 9)));  n += 1
    # favorite, archived, trashed, partner, video, size unknown
    fav = add_asset(n, "FAV_1.JPG", dt.datetime(2026, 9, 9, 9), favorite=True); n += 1
    add_asset(n, "ARCHIVED_1.JPG", dt.datetime(2026, 9, 10, 9), visibility="archive"); n += 1
    add_asset(n, "TRASHED_1.JPG", dt.datetime(2026, 9, 11, 9), trashed=True); n += 1
    partner = add_asset(n, "PARTNER_1.JPG", dt.datetime(2026, 9, 12, 9), owner=PARTNER); n += 1
    add_asset(n, "CLIP_1.MP4", dt.datetime(2026, 9, 13, 9), kind="VIDEO"); n += 1
    add_asset(n, "NOSIZE_1.JPG", dt.datetime(2026, 9, 14, 9), size_known=False); n += 1
    # live photo: image + hidden video
    video = add_asset(n, "IMG_LIVE.MOV", dt.datetime(2026, 9, 15, 9), kind="VIDEO", visibility="hidden"); n += 1
    add_asset(n, "IMG_LIVE.HEIC", dt.datetime(2026, 9, 15, 9), live_video=video); n += 1
    # month-boundary case: UTC is Nov 1 but local time (-5h) is Oct 31
    add_asset(n, "BOUNDARY.JPG", dt.datetime(2026, 11, 1, 2, 0), offset_hours=-5); n += 1

    trip = str(uuid.UUID(int=0xA1, version=4))
    ALBUMS[trip] = dict(name="Trip: Beach/Day", owner=ME, assets=set(ids[:4] + [fav]), updated=iso(dt.datetime(2026, 9, 1)))
    shared = str(uuid.UUID(int=0xA2, version=4))
    ALBUMS[shared] = dict(name="Family", owner=PARTNER, assets={partner, ids[5]}, updated=iso(dt.datetime(2026, 9, 2)))
    alice = str(uuid.UUID(int=0xB1, version=4))
    PEOPLE[alice] = dict(name="Alice", assets=set(ids[2:5]))
    PEOPLE[str(uuid.UUID(int=0xB2, version=4))] = dict(name="", assets={ids[6]})


def local_dt(a):
    return a["taken"] + dt.timedelta(hours=a["offset"])


def to_dto(a, with_exif=True):
    d = dict(
        id=a["id"], ownerId=a["owner"], originalFileName=a["name"], type=a["kind"],
        fileCreatedAt=iso(a["taken"]), fileModifiedAt=iso(a["taken"]), localDateTime=iso(local_dt(a)),
        createdAt=iso(a["taken"]), updatedAt=a["updated"], isFavorite=a["favorite"], isArchived=a["visibility"] == "archive",
        isTrashed=a["trashed"], isOffline=False, isEdited=False, visibility=a["visibility"],
        livePhotoVideoId=a["live"], checksum="x", duration=None, hasMetadata=True, height=100, width=100,
        originalPath="/x", thumbhash=None,
    )
    if with_exif:
        d["exifInfo"] = {"fileSizeInByte": len(a["data"]) if a["size_known"] else None}
    return d


def date_ok(value, cond):
    for op, raw in cond.items():
        if raw is None:
            continue
        ref = dt.datetime.strptime(raw[:19], "%Y-%m-%dT%H:%M:%S")
        if op == "gte" and not value >= ref: return False
        if op == "gt" and not value > ref: return False
        if op == "lt" and not value < ref: return False
        if op == "lte" and not value <= ref: return False
    return True


def matches(a, f):
    vis = f.get("visibility")
    if vis and "in" in vis and a["visibility"] not in vis["in"]: return False
    if vis and "ne" in vis and a["visibility"] == vis["ne"]: return False
    if not vis and a["visibility"] == "locked": return False
    typ = f.get("type")
    if typ and "in" in typ and a["kind"] not in typ["in"]: return False
    tr = f.get("trashedAt")
    if tr is not None and "eq" in tr and tr["eq"] is None and a["trashed"]: return False
    if "takenAt" in f and not date_ok(a["taken"], f["takenAt"]): return False
    if "isFavorite" in f and a["favorite"] != f["isFavorite"]["eq"]: return False
    if "albumIds" in f:
        wanted = f["albumIds"].get("any", [])
        if not any(a["id"] in ALBUMS[x]["assets"] for x in wanted if x in ALBUMS): return False
    if "personIds" in f:
        wanted = f["personIds"].get("any", [])
        if not any(a["id"] in PEOPLE[x]["assets"] for x in wanted if x in PEOPLE): return False
    return True


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (self.command, self.path))

    def send_json(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def auth(self):
        if self.headers.get("x-api-key") != API_KEY:
            self.send_json({"message": "Invalid API key"}, 401)
            return False
        return True

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        p = u.path
        if p == "/api/server/ping":
            return self.send_json({"res": "pong"})
        if not self.auth():
            return
        with lock:
            if p == "/api/server/about":
                return self.send_json({"version": "v3.3.5", "licensed": False, "versionUrl": ""})
            if p == "/api/users/me":
                return self.send_json({"id": ME, "name": "Test User", "email": "test@example.com"})
            if p == "/api/timeline/buckets":
                vis = q.get("visibility", "timeline")
                partners = q.get("withPartners") == "true"
                counts = {}
                for a in ASSETS.values():
                    if a["trashed"] or a["visibility"] != vis: continue
                    if a["owner"] != ME and not partners: continue
                    l = local_dt(a)
                    key = f"{l.year:04d}-{l.month:02d}-01"
                    counts[key] = counts.get(key, 0) + 1
                return self.send_json([{"timeBucket": k, "count": v} for k, v in sorted(counts.items(), reverse=True)])
            if p == "/api/albums":
                out = []
                for aid, al in ALBUMS.items():
                    out.append({"id": aid, "albumName": al["name"], "assetCount": len(al["assets"]), "updatedAt": al["updated"],
                                "albumUsers": [{"role": "editor", "user": {"id": al["owner"]}}], "shared": al["owner"] != ME})
                return self.send_json(out)
            if p == "/api/people":
                return self.send_json({"people": [{"id": pid, "name": pe["name"], "isHidden": False} for pid, pe in PEOPLE.items()],
                                       "hidden": 0, "total": len(PEOPLE), "hasNextPage": False})
            if p == "/api/assets/statistics":
                vis = q.get("visibility", "timeline")
                n = sum(1 for a in ASSETS.values() if a["visibility"] == vis and not a["trashed"])
                return self.send_json({"images": n, "videos": 0, "total": n})
            m = re.match(r"^/api/assets/([0-9a-f-]+)(/original|/thumbnail)?$", p)
            if m:
                a = ASSETS.get(m.group(1))
                if not a:
                    return self.send_json({"message": "Not found"}, 404)
                if m.group(2) is None:
                    return self.send_json(to_dto(a))
                if m.group(2) == "/thumbnail":
                    data = MEDIA[0] if a["kind"] == "VIDEO" else a["data"]
                    self.send_response(200)
                    self.send_header("Content-Type", "image/jpeg")
                    self.send_header("Content-Length", str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                    return
                data = a["data"]
        # /original with Range support (outside the lock)
        total = len(data)
        rng = self.headers.get("Range")
        start, end = 0, total - 1
        code = 200
        if rng:
            mm = re.match(r"bytes=(\d+)-(\d*)", rng)
            start = int(mm.group(1))
            end = int(mm.group(2)) if mm.group(2) else total - 1
            end = min(end, total - 1)
            code = 206
        chunk = data[start:end + 1]
        self.send_response(code)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(chunk)))
        if code == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{total}")
        self.send_header("Accept-Ranges", "bytes")
        self.end_headers()
        self.wfile.write(chunk)

    def do_POST(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        if u.path.startswith("/mock/"):
            with lock:
                if u.path == "/mock/add":
                    y, mo = map(int, q.get("month", "2026-10").split("-"))
                    ids = []
                    for _ in range(int(q.get("count", "1"))):
                        n = 10_000 + len(ASSETS)
                        name = q.get("name", f"NEW_{n}.JPG")
                        ids.append(add_asset(n, name, dt.datetime(y, mo, 20, 15) - dt.timedelta(minutes=len(ids))))
                    return self.send_json({"id": ids[0], "ids": ids})
                if u.path == "/mock/delete":
                    ASSETS.pop(q["id"], None)
                    return self.send_json({"ok": True})
            return self.send_json({"message": "bad mock call"}, 400)
        if not self.auth():
            return
        if u.path == "/api/search/metadata":
            b = self.body()
            f = b.get("filter") or {}
            size = min(int(b.get("size") or 250), PAGE_SIZE)
            offset = int(b.get("cursor") or 0)
            with lock:
                items = [a for a in ASSETS.values() if matches(a, f)]
                if "albumIds" not in f:
                    items = [a for a in items if a["owner"] in (ME, PARTNER)]
                items.sort(key=lambda a: (a["taken"], a["id"]), reverse=True)
                page = items[offset:offset + size]
                nxt = str(offset + size) if offset + size < len(items) else None
                dtos = [to_dto(a, b.get("withExif", False)) for a in page]
            return self.send_json({"albums": {"total": 0, "count": 0, "items": [], "facets": []},
                                   "assets": {"total": len(dtos), "count": len(dtos), "items": dtos, "facets": [],
                                              "nextPage": None, "nextCursor": nxt}})
        self.send_json({"message": "Not found"}, 404)


def main():
    global PAGE_SIZE
    ap = argparse.ArgumentParser()
    ap.add_argument("--media", required=True)
    ap.add_argument("--port", type=int, default=2283)
    ap.add_argument("--page-size", type=int, default=1000)
    args = ap.parse_args()
    PAGE_SIZE = args.page_size
    for name in sorted(os.listdir(args.media)):
        if name.lower().endswith((".jpg", ".jpeg")):
            with open(os.path.join(args.media, name), "rb") as fh:
                MEDIA.append(fh.read())
    if not MEDIA:
        sys.exit("no .jpg files in --media")
    seed()
    print(f"Mock Immich on http://127.0.0.1:{args.port} with {len(ASSETS)} assets (API key: {API_KEY})", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
