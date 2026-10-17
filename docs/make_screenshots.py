"""Renders README screenshots of the dashboard with realistic sample data (API mocked; no app instance needed)."""
import sys, json, math, random, pathlib
from playwright.sync_api import sync_playwright

www = pathlib.Path(sys.argv[1])
out = pathlib.Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)
random.seed(7)


def R(a, b):
    return random.uniform(a, b)


def mouse_summary(n):
    return {"n": n, "avgHz": 991.6, "medianHz": 1000.2, "nominalHz": 1000, "snapped": True, "estimated": False, "confidence": None,
            "meanUs": 1008.5, "medianUs": 999.8, "minUs": 871.2, "maxUs": 3011.4, "p1Us": 940.3, "p99Us": 1062.7,
            "jitterUs": 18.4, "withinPct": 97.8, "offCadencePct": 1.3, "pollWaitMs": 0.5, "hint": None}


def series(n, base, noise, dips=0.03, dip=0.8):
    return [round(base * (dip if random.random() < dips else 1) + R(-noise, noise), 2) for _ in range(n)]


def hist():
    bins = []
    for i in range(60):
        c = (i + 0.5) * 2500 / 60
        bins.append(int(9000 * math.exp(-((c - 1000) / 22) ** 2) + 400 * math.exp(-((c - 1000) / 60) ** 2)))
    bins[47] = 12
    bins[48] = 9
    return {"lo": 0, "hi": 2500, "bins": bins, "nominalUs": 1000, "total": sum(bins)}


def lat(avg, sd):
    return {"n": 412, "avg": avg, "p50": avg - 0.4, "p95": avg + 1.9 * sd, "p99": avg + 2.8 * sd, "min": avg - 2.2 * sd, "max": avg + 4 * sd}


frames = {"frames": 1203, "fps": 238.6, "onePctLow": 187.2, "ftAvg": 4.19, "ftP99": 5.34, "displayedFps": 238.1}
live_lat = {"frames": frames, "inputToPhoton": lat(13.6, 1.8), "clickToPhoton": lat(13.1, 1.6), "frameLatency": lat(8.9, 1.1)}
devices = [
    {"id": "10050", "name": "G Pro X Superlight 2", "vidPid": "046D:C09B", "connected": True, "liveHz": 991.6, "nominalHz": 1000, "reports": 184233, "active": True},
    {"id": "2004A", "name": "G103 Prodigy Gaming Mouse", "vidPid": "046D:C084", "connected": True, "liveHz": None, "nominalHz": None, "reports": 1260, "active": False},
]
snap = {
    "t": 912.3, "mode": "mouse", "admin": True, "hotkey": "Ctrl+Shift+F9", "registerError": None,
    "recording": {"active": True, "startedAt": "2026-10-17T10:02:00Z", "elapsed": 754.2}, "lastSavedId": "20261017-174511",
    "selected": "auto", "devices": devices,
    "device": {
        "id": "10050", "name": "G Pro X Superlight 2", "manufacturer": "Logitech", "vidPid": "046D:C09B",
        "path": "\\\\?\\HID#VID_046D&PID_C547&MI_01&Col01#8&2f1e0a3&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}",
        "connected": True, "reports": 184233, "gaps": 2190, "live": mouse_summary(1974), "session": mouse_summary(731204),
        "liveSeries": {"a": series(240, 1000, 3, 0.02, 0.9), "b": series(240, 990, 9, 0.06, 0.85), "step": 0.25},
        "sessionSeries": {"a": series(1500, 1000, 3, 0.02, 0.9), "b": series(1500, 990, 10, 0.07, 0.85), "step": 0.5},
        "liveHist": hist(), "sessionHist": hist(),
        "keys": {"presses": 1288, "tapAvgMs": 71.4, "tapMinMs": 38.2, "tapMaxMs": 912.6, "chatter": 1,
                 "chatterLog": [{"key": "Mouse L", "gapMs": 4.12, "atSec": 431.7}]},
        "hasPad": False,
    },
    "latency": {
        "available": True, "exe": "tools\\PresentMon-2.3.1-x64.exe", "admin": True, "running": True, "status": "running",
        "error": None, "download": None, "target": "game.exe", "targetMode": "auto",
        "apps": [{"name": "game.exe", "fps": 238.6, "target": True}, {"name": "msedge.exe", "fps": 60.0, "target": False},
                 {"name": "Discord.exe", "fps": 14.3, "target": False}],
        "live": live_lat, "session": live_lat,
        "series": {"input": series(240, 13.6, 1.6, 0.0), "frameLatency": series(240, 8.9, 0.8, 0.0), "fps": series(240, 238, 6, 0.03, 0.82), "step": 0.25},
        "sessionSeries": {"input": series(1500, 13.6, 1.6, 0.0), "frameLatency": series(1500, 8.9, 0.8, 0.0), "fps": series(1500, 238, 6, 0.03, 0.82), "step": 0.5},
        "chain": {"measured": True, "totalMs": 14.1, "segments": [
            {"label": "Device poll wait", "ms": 0.5, "src": "estimated from polling rate"},
            {"label": "OS input → photon", "ms": 13.6, "src": "measured by PresentMon"}]},
        "cpuPct": 0.21,
    },
    "overhead": {"cpuPct": 0.14, "cpuCorePct": 2.2, "memMb": 71, "eventsPerSec": 996, "dropped": 0, "ringFill": 3, "presentMonCpuPct": 0.21, "cores": 16},
}
sessions = [
    {"id": "20261017-174511", "startedAt": "2026-10-17T09:32:50Z", "durationSec": 754.2, "mode": "mouse",
     "headline": {"device": "G Pro X Superlight 2", "avgHz": 991.6, "nominalHz": 1000, "jitterUs": 18.4, "lagMs": 13.6, "fps": 238.6, "target": "game.exe"}},
    {"id": "20261017-171204", "startedAt": "2026-10-17T09:12:04Z", "durationSec": 1311.0, "mode": "controller",
     "headline": {"device": "DualSense Wireless Controller", "avgHz": 249.8, "nominalHz": 250, "jitterUs": 41.0, "lagMs": None, "fps": 143.9, "target": "game.exe"}},
    {"id": "20261016-221930", "startedAt": "2026-10-16T14:19:30Z", "durationSec": 412.5, "mode": "keyboard",
     "headline": {"device": "Wooting 60HE", "avgHz": None, "nominalHz": 1000, "jitterUs": None, "lagMs": 12.9, "fps": 240.2, "target": "game.exe"}},
]
session_doc = {
    "id": "20261017-174511", "version": 1, "startedAt": "2026-10-17T09:32:50Z", "endedAt": "2026-10-17T09:45:24Z",
    "durationSec": 754.2, "mode": "mouse", "headline": sessions[0]["headline"],
    "devices": [{"id": "10050", "name": "G Pro X Superlight 2", "manufacturer": "Logitech", "vidPid": "046D:C09B",
                 "summary": mouse_summary(731204), "series": snap["device"]["sessionSeries"], "hist": hist(),
                 "keys": snap["device"]["keys"]}],
    "latency": {"target": "game.exe", "stats": live_lat, "series": snap["latency"]["sessionSeries"]},
}


def trail(fx, n=60):
    pts = []
    for i in range(n):
        pts.extend(fx(i / n))
    return pts


pad = {
    "name": "DualSense Wireless Controller", "ok": True,
    "mapping": "Right stick = Z/Rz, triggers = Rx/Ry (PlayStation / DirectInput layout)",
    "parsed": 48211, "ls": {"x": 0.62, "y": -0.55}, "rs": {"x": 0.012, "y": -0.009}, "lt": 0.0, "rt": 0.73, "combinedTriggers": False,
    "hat": 2, "buttons": [2, 6], "buttonCount": 14,
    "axes": [{"name": n, "v": v} for n, v in [("X", 0.81), ("Y", 0.225), ("Z", 0.506), ("Rx", 0.0), ("Ry", 0.73), ("Rz", 0.495)]],
    "trailL": trail(lambda t: (0.83 * math.cos(-0.73 + t * 4.6), 0.83 * math.sin(-0.73 + t * 4.6))), "trailR": [],
    "circL": {"bins": [1.0 + 0.045 * abs(math.sin(2 * (i + .5) * 2 * math.pi / 72)) for i in range(72)], "coverage": 1, "errorPct": 2.9, "avgReach": 1.03},
    "circR": {"bins": [1.0 + 0.11 * abs(math.sin(2 * (i + .5) * 2 * math.pi / 72)) if i < 66 else 0 for i in range(72)], "coverage": 0.92, "errorPct": 7.1, "avgReach": 1.07},
    "drift": {"active": False, "remaining": 0, "seconds": 5, "result": {
        "idleOnly": False, "ltRestMax": 0.0, "rtRestMax": 0.0,
        "left": {"samples": 1247, "offsetX": 0.006, "offsetY": -0.004, "offset": 0.0072, "noise": 0.0031, "maxDev": 0.0158, "suggestedDeadzonePct": 3, "verdict": "Excellent"},
        "right": {"samples": 1247, "offsetX": -0.061, "offsetY": 0.047, "offset": 0.077, "noise": 0.0094, "maxDev": 0.104, "suggestedDeadzonePct": 12, "verdict": "Drift detected"}}},
}
ctrl = json.loads(json.dumps(snap))
ctrl.update(mode="controller", recording={"active": False, "startedAt": None, "elapsed": 0})
ctrl["devices"] = [{"id": "3A0C2", "name": "DualSense Wireless Controller", "vidPid": "054C:0CE6", "connected": True, "liveHz": 249.8, "nominalHz": 250, "reports": 52011, "active": True}]
ctrl["device"].update(id="3A0C2", name="DualSense Wireless Controller", manufacturer="Sony Interactive Entertainment", vidPid="054C:0CE6", hasPad=True)

current = {"snap": snap}
TYPES = {".js": "text/javascript", ".css": "text/css", ".woff": "font/woff", ".svg": "image/svg+xml", ".html": "text/html"}


def sse(obj):
    return "retry: 600000\ndata: " + json.dumps(obj) + "\n\n"


def handler(route):
    path = route.request.url.split("plt.local", 1)[1].split("?")[0]
    if path == "/api/stream":
        return route.fulfill(status=200, headers={"content-type": "text/event-stream"}, body=sse(current["snap"]))
    if path == "/api/gamepad/stream":
        return route.fulfill(status=200, headers={"content-type": "text/event-stream"}, body=sse(pad))
    if path == "/api/sessions":
        return route.fulfill(status=200, content_type="application/json", body=json.dumps(sessions))
    if path.startswith("/api/sessions/"):
        return route.fulfill(status=200, content_type="application/json", body=json.dumps(session_doc))
    if path.startswith("/api/"):
        return route.fulfill(status=200, content_type="application/json", body="true")
    f = www / ("index.html" if path == "/" else path.lstrip("/"))
    if not f.exists():
        return route.fulfill(status=404, body="")
    route.fulfill(status=200, headers={"content-type": TYPES.get(f.suffix, "application/octet-stream")}, body=f.read_bytes())


with sync_playwright() as p:
    b = p.chromium.launch(channel="msedge")

    def page(w, h, dpr=1):
        pg = b.new_page(viewport={"width": w, "height": h}, device_scale_factor=dpr)
        pg.route("http://plt.local/**", handler)
        errs = []
        pg.on("pageerror", lambda e: errs.append(str(e)))
        return pg, errs

    current["snap"] = snap
    pg, errs = page(1600, 1240)
    pg.goto("http://plt.local/")
    pg.wait_for_timeout(1500)
    pg.screenshot(path=str(out / "dashboard.png"))
    pg.locator("[data-win=poll]").screenshot(path=str(out / "polling.png"))
    pg.locator(".col-right").screenshot(path=str(out / "input-lag.png"))
    pg.evaluate("openSession('20261017-174511')")
    pg.wait_for_timeout(600)
    pg.locator("#dlg-session").screenshot(path=str(out / "session.png"))
    print("desktop errors", errs)

    current["snap"] = ctrl
    pg, errs = page(1600, 1300)
    pg.goto("http://plt.local/")
    pg.wait_for_timeout(1500)
    pg.locator("#w-lab").screenshot(path=str(out / "controller-lab.png"))
    print("controller errors", errs)

    current["snap"] = snap
    pg, errs = page(390, 844, 2)
    pg.goto("http://plt.local/")
    pg.wait_for_timeout(1500)
    pg.screenshot(path=str(out / "phone.png"))
    print("phone errors", errs)
    b.close()
