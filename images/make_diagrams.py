#!/usr/bin/env python3
"""Draw the three diagrams of the post from the Azure Public Service Icons set.

    python images/make_diagrams.py "<path to>/Azure_Public_Service_Icons/Icons"

Writes SVG files next to this script and, when `rsvg-convert` is on PATH,
PNG files at twice the size for the blog.
"""

from __future__ import annotations

import re
import shutil
import subprocess
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent

ICONS = {
    "function": "compute/10029-icon-service-Function-Apps.svg",
    "apim": "integration/10042-icon-service-API-Management-Services.svg",
    "servicebus": "integration/10836-icon-service-Azure-Service-Bus.svg",
    "storage": "storage/10086-icon-service-Storage-Accounts.svg",
    "table": "general/10841-icon-service-Table.svg",
    "insights": "monitor/00012-icon-service-Application-Insights.svg",
    "identity": "identity/10227-icon-service-Managed-Identities.svg",
    "client": "general/10825-icon-service-Powershell.svg",
    "browser": "general/10783-icon-service-Browser.svg",
}

INK = "#1b2733"
MUTED = "#5b6b7b"
LINE = "#c9d3dd"
CARD = "#ffffff"
PAGE = "#f6f8fa"
BLUE = "#0078d4"
GREEN = "#1a7f37"
RED = "#c62828"
AMBER = "#b26a00"
FONT = "Inter, 'Segoe UI', Roboto, Arial, sans-serif"


class Canvas:
    """Collects SVG fragments; every embedded icon gets its ids prefixed so gradients never collide."""

    def __init__(self, icon_root: Path, width: int, height: int):
        self.icon_root = icon_root
        self.width, self.height = width, height
        self.parts: list[str] = []
        self.icon_count = 0

    # ----- primitives -----

    def icon(self, key: str, x: float, y: float, size: float = 48) -> None:
        raw = (self.icon_root / ICONS[key]).read_text(encoding="utf-8")
        view_box = re.search(r'viewBox="([^"]+)"', raw).group(1)
        inner = re.search(r"<svg[^>]*>(.*)</svg>", raw, re.S).group(1)
        inner = re.sub(r"<title>.*?</title>", "", inner, flags=re.S)
        self.icon_count += 1
        prefix = f"i{self.icon_count}_"
        inner = re.sub(r'id="([^"]+)"', lambda m: f'id="{prefix}{m.group(1)}"', inner)
        inner = re.sub(r"url\(#([^)]+)\)", lambda m: f"url(#{prefix}{m.group(1)})", inner)
        inner = re.sub(r'href="#([^"]+)"', lambda m: f'href="#{prefix}{m.group(1)}"', inner)
        self.parts.append(
            f'<svg x="{x}" y="{y}" width="{size}" height="{size}" viewBox="{view_box}">{inner}</svg>')

    def text(self, x: float, y: float, value: str, size: int = 15, weight: int = 400,
             fill: str = INK, anchor: str = "start") -> None:
        value = value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        self.parts.append(
            f'<text x="{x}" y="{y}" font-family="{FONT}" font-size="{size}" font-weight="{weight}" '
            f'fill="{fill}" text-anchor="{anchor}">{value}</text>')

    def rect(self, x: float, y: float, w: float, h: float, fill: str = CARD, stroke: str = LINE,
             radius: int = 12, width: float = 1.5, dash: str = "") -> None:
        dash_attr = f' stroke-dasharray="{dash}"' if dash else ""
        self.parts.append(
            f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{radius}" fill="{fill}" '
            f'stroke="{stroke}" stroke-width="{width}"{dash_attr}/>')

    def card(self, x: float, y: float, w: float, h: float, icon: str, title: str, lines: list[str]) -> None:
        """Uniform icon card: icon on top, bold title, muted lines, all centered."""
        self.rect(x, y, w, h)
        self.icon(icon, x + w / 2 - 24, y + 18)
        self.text(x + w / 2, y + 92, title, 16, 700, anchor="middle")
        for index, line in enumerate(lines):
            self.text(x + w / 2, y + 114 + index * 19, line, 13, 400, MUTED, "middle")

    def arrow(self, points: list[tuple[float, float]], color: str = INK, dash: str = "", width: float = 2) -> None:
        path = "M " + " L ".join(f"{px} {py}" for px, py in points)
        dash_attr = f' stroke-dasharray="{dash}"' if dash else ""
        marker = {INK: "ink", BLUE: "blue", GREEN: "green", RED: "red", AMBER: "amber"}[color]
        self.parts.append(
            f'<path d="{path}" fill="none" stroke="{color}" stroke-width="{width}" stroke-linejoin="round"'
            f'{dash_attr} marker-end="url(#arrow-{marker})"/>')

    def pill(self, x: float, y: float, value: str, fill: str, color: str = "#ffffff", size: int = 13) -> None:
        w = len(value) * size * 0.6 + 22
        self.parts.append(f'<rect x="{x}" y="{y}" width="{w}" height="26" rx="13" fill="{fill}"/>')
        self.text(x + w / 2, y + 18, value, size, 700, color, "middle")

    def number(self, x: float, y: float, value: str, fill: str = INK) -> None:
        self.parts.append(f'<circle cx="{x}" cy="{y}" r="14" fill="{fill}"/>')
        self.text(x, y + 5, value, 14, 700, "#ffffff", "middle")

    def circle(self, cx: float, cy: float, r: float, stroke: str, fill: str) -> None:
        self.parts.append(f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{fill}" stroke="{stroke}" stroke-width="3"/>')

    # ----- output -----

    def save(self, name: str) -> None:
        markers = "".join(
            f'<marker id="arrow-{key}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" '
            f'orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="{color}"/></marker>'
            for key, color in (("ink", INK), ("blue", BLUE), ("green", GREEN), ("red", RED), ("amber", AMBER)))
        svg = (
            f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" '
            f'width="{self.width}" height="{self.height}" viewBox="0 0 {self.width} {self.height}">'
            f"<defs>{markers}</defs>"
            f'<rect width="100%" height="100%" fill="{PAGE}"/>' + "".join(self.parts) + "</svg>")
        target = OUT / f"{name}.svg"
        target.write_text(svg, encoding="utf-8")
        if shutil.which("rsvg-convert"):
            subprocess.run(["rsvg-convert", "-z", "2", "-o", str(OUT / f"{name}.png"), str(target)], check=True)
        print(f"wrote {target.name}")


def title(c: Canvas, heading: str, sub: str) -> None:
    c.text(50, 58, heading, 28, 700)
    c.text(50, 88, sub, 16, 400, MUTED)


# ---------- 1. Architecture ----------

def architecture(icon_root: Path) -> None:
    c = Canvas(icon_root, 1600, 930)
    title(c, "Circuit breaker on Azure: four ways to call a failing dependency",
          "One order API, one unreliable payment provider. The path decides what an outage costs.")

    # Left to right: client, orders, three lanes, provider.
    c.card(50, 255, 190, 150, "client", "Test client", ["scripts/drive.py"])
    c.arrow([(240, 330), (330, 330)])
    c.text(285, 318, "POST", 12, 700, MUTED, "middle")

    c.rect(330, 130, 250, 400)
    c.icon("function", 431, 150)
    c.text(455, 226, "Orders API", 17, 700, anchor="middle")
    c.text(455, 247, "Function App, Flex Consumption", 13, 400, MUTED, "middle")
    c.text(455, 290, "Saves the order first,", 13, 400, MUTED, "middle")
    c.text(455, 309, "then tries to charge", 13, 400, MUTED, "middle")
    c.text(455, 500, "?path=naive | breaker | gateway | fallback", 11, 400, MUTED, "middle")

    c.rect(1300, 130, 250, 400)
    c.icon("function", 1401, 150)
    c.text(1425, 226, "Payment provider", 17, 700, anchor="middle")
    c.text(1425, 247, "Function App (stub)", 13, 400, MUTED, "middle")
    c.text(1425, 290, "Fails on command:", 13, 400, MUTED, "middle")
    c.text(1425, 309, "down, slow, slow-success, flaky", 13, 400, MUTED, "middle")
    c.text(1425, 500, "Counts every call that reaches it", 12, 400, MUTED, "middle")

    # Lane 1: naive
    c.arrow([(580, 190), (1300, 190)], RED)
    c.number(612, 190, "1", RED)
    c.text(940, 176, "naive: plain HttpClient, every caller waits for the provider", 14, 600, RED, "middle")

    # Lane 2: in-process breaker
    c.arrow([(580, 330), (840, 330)], BLUE)
    c.rect(840, 290, 200, 80, "#eaf3fb", BLUE)
    c.text(940, 323, "Circuit breaker", 15, 700, BLUE, "middle")
    c.text(940, 344, "Polly, one per instance", 13, 400, MUTED, "middle")
    c.arrow([(1040, 330), (1300, 330)], BLUE)
    c.number(612, 330, "2", BLUE)
    c.text(710, 316, "breaker", 14, 600, BLUE, "middle")
    c.text(1170, 316, "2 s attempt timeout", 13, 400, MUTED, "middle")

    # Lane 3: gateway breaker
    c.arrow([(580, 455), (840, 455)], AMBER)
    c.rect(840, 400, 200, 110, "#fff7e6", AMBER)
    c.icon("apim", 858, 414, 40)
    c.text(908, 432, "API Management", 14, 700, AMBER)
    c.text(908, 451, "Basic v2", 12, 400, MUTED)
    c.text(940, 480, "Breaker rule on the backend,", 12, 400, MUTED, "middle")
    c.text(940, 497, "answers 503 while tripped", 12, 400, MUTED, "middle")
    c.arrow([(1040, 455), (1300, 455)], AMBER)
    c.number(612, 455, "3", AMBER)
    c.text(710, 441, "gateway", 14, 600, AMBER, "middle")

    # Lane 4: fallback
    c.arrow([(455, 530), (455, 640)], GREEN)
    c.number(455, 572, "4", GREEN)
    c.text(480, 600, "fallback: breaker open? Park the payment, answer 202", 14, 600, GREEN)
    c.card(330, 640, 250, 150, "servicebus", "Service Bus queue", ["payments-pending"])
    c.arrow([(580, 715), (760, 715)], GREEN)
    c.card(760, 640, 250, 150, "function", "SettlePayment", ["Service Bus trigger,", "same breaker as path 2"])
    c.arrow([(1010, 690), (1425, 690), (1425, 530)], GREEN)
    c.text(1220, 676, "charge with the same idempotency key", 13, 400, MUTED, "middle")
    c.arrow([(885, 790), (885, 830), (455, 830), (455, 790)], GREEN, "6 5")
    c.text(670, 852, "breaker still open: reschedule with a delay, do not throw", 13, 600, GREEN, "middle")

    # Shared services
    c.rect(1100, 745, 450, 130, "#ffffff", LINE, dash="5 5")
    c.text(1120, 772, "Shared by both apps", 13, 700, MUTED)
    for index, (key, label) in enumerate((("table", "Table Storage"), ("insights", "Application Insights"),
                                         ("identity", "Managed identity"))):
        x = 1130 + index * 145
        c.icon(key, x + 36, 786, 36)
        c.text(x + 54, 846, label, 12, 400, INK, "middle")

    c.save("architecture")


# ---------- 2. States ----------

def states(icon_root: Path) -> None:
    c = Canvas(icon_root, 1600, 800)
    title(c, "Three states in the process, two at the gateway",
          "The demo values. A real threshold is a ratio over a window, with a minimum number of calls.")

    def state(cx: float, cy: float, name: str, line: str, stroke: str, fill: str) -> None:
        c.circle(cx, cy, 92, stroke, fill)
        c.text(cx, cy - 4, name, 20, 700, anchor="middle")
        c.text(cx, cy + 22, line, 13, 400, MUTED, "middle")

    # Row A: Polly
    c.rect(50, 125, 1500, 330)
    c.icon("function", 76, 146, 40)
    c.text(128, 166, "In the process: Polly circuit breaker", 18, 700)
    c.text(128, 187, "One breaker per Function App instance. Instances do not share state.", 13, 400, MUTED)

    state(400, 325, "CLOSED", "calls flow", GREEN, "#e9f6ec")
    state(800, 325, "OPEN", "fail in microseconds", RED, "#fdecec")
    state(1200, 325, "HALF-OPEN", "one probe call", AMBER, "#fff4dd")

    c.arrow([(492, 300), (708, 300)], RED)
    c.text(600, 268, "50% or more of at least 10 calls", 13, 600, RED, "middle")
    c.text(600, 286, "failed within 10 s", 13, 600, RED, "middle")
    c.arrow([(892, 300), (1108, 300)], AMBER)
    c.text(1000, 286, "after 15 s", 13, 600, AMBER, "middle")
    c.arrow([(1108, 350), (892, 350)], RED, "6 5")
    c.text(1000, 376, "probe fails: open again", 13, 600, RED, "middle")
    c.arrow([(1200, 417), (1200, 438), (400, 438), (400, 419)], GREEN)
    c.rect(700, 425, 200, 26, "#e9f6ec", GREEN, 13)
    c.text(800, 443, "probe succeeds", 13, 700, GREEN, "middle")

    c.text(1330, 300, "Counts as failure:", 13, 700)
    c.text(1330, 320, "5xx, 408, 429,", 13, 400, MUTED)
    c.text(1330, 338, "timeouts, connection errors", 13, 400, MUTED)
    c.text(1330, 364, "Does not count: 400, 404", 13, 400, MUTED)

    # Row B: API Management
    c.rect(50, 480, 1500, 280)
    c.icon("apim", 76, 500, 40)
    c.text(128, 520, "At the gateway: API Management backend circuit breaker", 18, 700)
    c.text(128, 541, "One rule per backend. Gateway instances do not synchronize, so tripping is approximate.", 13, 400, MUTED)

    state(400, 655, "CLOSED", "calls flow", GREEN, "#e9f6ec")
    state(800, 655, "TRIPPED", "503 from the gateway", RED, "#fdecec")
    c.arrow([(492, 630), (708, 630)], RED)
    c.text(600, 616, "5 answers in 500-599 within 10 s", 13, 600, RED, "middle")
    c.arrow([(708, 680), (492, 680)], GREEN)
    c.text(600, 706, "after 15 s: all traffic resumes", 13, 600, GREEN, "middle")

    c.text(1000, 630, "No half-open state.", 15, 700)
    c.text(1000, 654, "After the trip duration the full load returns at once.", 13, 400, MUTED)
    c.text(1000, 674, "If the provider is still down, five more calls fail", 13, 400, MUTED)
    c.text(1000, 694, "before the circuit trips again.", 13, 400, MUTED)

    c.save("states")


# ---------- 3. Fallback flow ----------

def fallback(icon_root: Path) -> None:
    c = Canvas(icon_root, 1600, 560)
    title(c, "Payment is down, the customer already ordered",
          "The breaker decides when to stop calling. The fallback decides what happens to the order.")

    steps = [
        ("table", "Save the order", ["Status Created, before", "any payment call"]),
        ("function", "Breaker rejects", ["Circuit is open: no call", "leaves the process"]),
        ("servicebus", "Park the payment", ["Queue message, order is", "PaymentPending, HTTP 202"]),
        ("function", "Settle, or wait", ["Still open? Reschedule", "15 s plus jitter"]),
        ("function", "Provider recovers", ["Probe succeeds, the", "circuit closes"]),
        ("table", "Charged once", ["Idempotency key = order id,", "order becomes Paid"]),
    ]
    width, gap, top = 220, 36, 180
    for index, (icon, heading, lines) in enumerate(steps):
        x = 50 + index * (width + gap)
        c.card(x, top, width, 170, icon, heading, lines)
        c.number(x + 24, top + 24, str(index + 1), GREEN if index >= 2 else INK)
        if index < len(steps) - 1:
            c.arrow([(x + width + 3, top + 85), (x + width + gap - 3, top + 85)])

    # The loop on step 4 and the two timelines underneath.
    x4 = 50 + 3 * (width + gap)
    c.arrow([(x4 + 150, top + 170), (x4 + 150, top + 205), (x4 + 70, top + 205), (x4 + 70, top + 172)], GREEN, "6 5")
    c.text(x4 + 110, top + 226, "repeat while open", 12, 600, GREEN, "middle")

    c.rect(50, 440, 732, 80, "#eaf3fb", BLUE)
    c.text(70, 470, "The customer sees", 13, 700, BLUE)
    c.text(70, 494, "\"Order received. We are confirming your payment.\" Within milliseconds, during the outage.", 13, 400, INK)
    c.rect(818, 440, 732, 80, "#e9f6ec", GREEN)
    c.text(838, 470, "The business gets", 13, 700, GREEN)
    c.text(838, 494, "Every order kept, every order charged exactly once, no dead-letter queue to clean up.", 13, 400, INK)

    c.save("fallback-flow")


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    icon_root = Path(sys.argv[1])
    if not icon_root.is_dir():
        print(f"Icon folder not found: {icon_root}", file=sys.stderr)
        return 2
    architecture(icon_root)
    states(icon_root)
    fallback(icon_root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
