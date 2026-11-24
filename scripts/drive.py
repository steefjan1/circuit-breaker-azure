#!/usr/bin/env python3
"""Drive the circuit breaker demo and measure what each path does during an outage.

Standard library only. Usage:

    python scripts/drive.py --orders-url https://<orders-app>/api --payment-url https://<payment-app>/api
    python scripts/drive.py --only fallback,requeue        # run a subset
    python scripts/drive.py --list                         # show the scenarios

Without the URL arguments the script reads ORDERS_URL and PAYMENT_URL from the
environment, then from `azd env get-values`.

Every scenario uses its own run id, so the counts per scenario never mix:
orders are counted by the orders app, calls that reached the payment provider
are counted by the payment stub.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field

# Must match the app settings in infra/resources.bicep.
BREAK_SECONDS = 15
ATTEMPT_TIMEOUT_SECONDS = 2


# ---------- HTTP helpers ----------

def call(method: str, url: str, body: dict | None = None, timeout: float = 60.0) -> tuple[int, dict, float]:
    """Return (status, json body, elapsed ms). Never raises for HTTP errors."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        return 0, {"error": str(error)}, (time.perf_counter() - started) * 1000
    elapsed = (time.perf_counter() - started) * 1000
    try:
        return status, json.loads(raw or b"{}"), elapsed
    except json.JSONDecodeError:
        return status, {"raw": raw.decode(errors="replace")[:200]}, elapsed


def percentile(values: list[float], pct: float) -> float:
    """Nearest-rank percentile; 0 for an empty list."""
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, round(pct / 100 * len(ordered) + 0.5) - 1))
    return ordered[index]


# ---------- Demo client ----------

@dataclass
class Burst:
    """Result of a batch of orders."""
    statuses: dict[int, int] = field(default_factory=dict)
    outcomes: dict[str, int] = field(default_factory=dict)
    instances: set[str] = field(default_factory=set)
    latencies: list[float] = field(default_factory=list)
    wall_seconds: float = 0.0

    def summary(self) -> dict:
        return {
            "http": dict(sorted(self.statuses.items())),
            "outcomes": dict(sorted(self.outcomes.items())),
            "instances": len(self.instances),
            "p50_ms": round(statistics.median(self.latencies)) if self.latencies else 0,
            "p95_ms": round(percentile(self.latencies, 95)),
            "wall_s": round(self.wall_seconds, 1),
        }


class Demo:
    def __init__(self, orders_url: str, payment_url: str):
        self.orders = orders_url.rstrip("/")
        self.payment = payment_url.rstrip("/")

    def set_mode(self, mode: str, delay_ms: int = 8000, failure_rate: float = 0.5) -> None:
        status, body, _ = call("POST", f"{self.payment}/control/mode",
                               {"mode": mode, "delayMs": delay_ms, "failureRate": failure_rate})
        if status != 200:
            raise RuntimeError(f"set mode {mode} failed: {status} {body}")

    def order(self, path: str, run_id: str, settle: str = "reschedule") -> tuple[int, dict, float]:
        return call("POST", f"{self.orders}/orders?path={path}&runId={run_id}&settle={settle}", {"amount": 49.95})

    def burst(self, path: str, run_id: str, count: int, concurrency: int, settle: str = "reschedule") -> Burst:
        result = Burst()
        started = time.perf_counter()
        with ThreadPoolExecutor(max_workers=concurrency) as pool:
            for status, body, elapsed in pool.map(lambda _: self.order(path, run_id, settle), range(count)):
                result.statuses[status] = result.statuses.get(status, 0) + 1
                outcome = body.get("outcome", "none")
                result.outcomes[outcome] = result.outcomes.get(outcome, 0) + 1
                if "instance" in body:
                    result.instances.add(body["instance"])
                result.latencies.append(elapsed)
        result.wall_seconds = time.perf_counter() - started
        return result

    def stub_stats(self, run_id: str) -> dict:
        return call("GET", f"{self.payment}/control/stats?runId={run_id}")[1].get("stats", {})

    def order_counts(self, run_id: str) -> dict:
        return call("GET", f"{self.orders}/summary?runId={run_id}")[1].get("counts", {})

    def queue(self) -> dict:
        return call("GET", f"{self.orders}/queue")[1]

    def recover(self) -> None:
        """Back to a healthy provider with closed breakers before the next scenario."""
        self.set_mode("healthy")
        time.sleep(BREAK_SECONDS + 5)
        warm = "warm-" + uuid.uuid4().hex[:6]
        self.burst("breaker", warm, 20, 4)
        self.burst("gateway", warm, 5, 1)


def new_run(name: str) -> str:
    return f"{name}-{uuid.uuid4().hex[:6]}"


def wait_until(predicate, timeout_s: float, every_s: float = 2.0) -> float | None:
    """Poll until predicate() is true. Returns the elapsed seconds, or None on timeout."""
    started = time.perf_counter()
    while time.perf_counter() - started < timeout_s:
        if predicate():
            return time.perf_counter() - started
        time.sleep(every_s)
    return None


# ---------- Scenarios ----------
# Each scenario returns (measurements, list of failed checks).

def baseline(demo: Demo) -> tuple[dict, list[str]]:
    """Healthy provider: every path pays."""
    run = new_run("baseline")
    demo.set_mode("healthy")
    burst = demo.burst("breaker", run, 40, 8)
    stats = demo.stub_stats(run)
    failed = [] if burst.statuses.get(201) == 40 else ["expected 40 paid orders"]
    return {"orders": burst.summary(), "provider": stats}, failed


def outage_naive(demo: Demo) -> tuple[dict, list[str]]:
    """Slow provider, no breaker: every caller waits the full delay."""
    run = new_run("naive")
    demo.set_mode("slow", delay_ms=8000)
    burst = demo.burst("naive", run, 40, 8)
    time.sleep(10)  # let the slow calls finish so the stub has counted them
    stats = demo.stub_stats(run)
    failed = []
    if burst.summary()["p50_ms"] < 7000:
        failed.append("expected callers to wait about 8 s")
    if stats.get("received", 0) < 40:
        failed.append("expected all 40 calls to reach the provider")
    return {"orders": burst.summary(), "provider": stats}, failed


def outage_breaker(demo: Demo) -> tuple[dict, list[str]]:
    """Slow provider, in-process breaker: a few slow calls, then fast failures."""
    run = new_run("breaker")
    demo.set_mode("slow", delay_ms=8000)
    burst = demo.burst("breaker", run, 200, 8)
    time.sleep(10)
    stats = demo.stub_stats(run)
    failed = []
    if burst.outcomes.get("CircuitOpen", 0) == 0:
        failed.append("the breaker never opened")
    if stats.get("received", 0) >= 200:
        failed.append("the breaker stopped no calls")
    return {"orders": burst.summary(), "provider": stats,
            "note": "instances > 1 means several breakers each learned about the outage on their own"}, failed


def outage_gateway(demo: Demo) -> tuple[dict, list[str]]:
    """Provider down, API Management holds the breaker."""
    run = new_run("gateway")
    demo.set_mode("down")
    burst = demo.burst("gateway", run, 200, 8)
    if burst.statuses.get(501):
        return {"skipped": "API Management is not deployed"}, []
    time.sleep(3)
    stats = demo.stub_stats(run)
    failed = []
    if burst.outcomes.get("GatewayCircuitOpen", 0) == 0:
        failed.append("the gateway breaker never tripped")
    return {"orders": burst.summary(), "provider": stats}, failed


def fallback(demo: Demo) -> tuple[dict, list[str]]:
    """Provider down, orders accepted and paid after recovery."""
    run = new_run("fallback")
    demo.set_mode("down")
    burst = demo.burst("fallback", run, 100, 8)
    during = demo.order_counts(run)
    demo.set_mode("healthy")
    drained = wait_until(lambda: demo.order_counts(run).get("Paid", 0) == 100, timeout_s=300)
    stats = demo.stub_stats(run)
    failed = []
    if burst.statuses.get(202) != 100:
        failed.append("expected 100 accepted orders")
    if drained is None:
        failed.append("not all orders were paid within 5 minutes")
    if stats.get("charged") != 100:
        failed.append(f"expected exactly 100 charges, got {stats.get('charged')}")
    return {"orders": burst.summary(), "during_outage": during,
            "seconds_until_all_paid": None if drained is None else round(drained, 1),
            "after": demo.order_counts(run), "provider": stats}, failed


def requeue(demo: Demo) -> tuple[dict, list[str]]:
    """Open breaker in a queue consumer: throwing dead-letters, rescheduling does not."""
    before = demo.queue().get("deadLetter", 0)
    demo.set_mode("down")

    throw_run = new_run("throw")
    demo.burst("fallback", throw_run, 20, 4, settle="throw")
    dead_after = wait_until(lambda: demo.queue().get("deadLetter", 0) - before >= 20, timeout_s=120, every_s=1.0)
    dead_throw = demo.queue().get("deadLetter", 0) - before

    resched_run = new_run("reschedule")
    demo.burst("fallback", resched_run, 20, 4, settle="reschedule")
    time.sleep(45)  # three break durations of outage
    dead_resched = demo.queue().get("deadLetter", 0) - before - dead_throw
    parked = demo.queue()

    demo.set_mode("healthy")
    drained = wait_until(lambda: demo.order_counts(resched_run).get("Paid", 0) == 20, timeout_s=180)

    failed = []
    if dead_throw < 20:
        failed.append(f"expected 20 dead-lettered messages in throw mode, got {dead_throw}")
    if dead_resched != 0:
        failed.append(f"expected 0 dead-lettered messages in reschedule mode, got {dead_resched}")
    if drained is None:
        failed.append("rescheduled payments did not settle after recovery")
    return {
        "throw": {"dead_lettered": dead_throw,
                  "seconds_until_all_dead_lettered": None if dead_after is None else round(dead_after, 1),
                  "orders": demo.order_counts(throw_run)},
        "reschedule": {"dead_lettered": dead_resched, "queue_during_outage": parked,
                       "seconds_until_all_paid_after_recovery": None if drained is None else round(drained, 1),
                       "orders": demo.order_counts(resched_run)},
    }, failed


def ambiguous(demo: Demo) -> tuple[dict, list[str]]:
    """Provider is slow but does charge: the caller times out, the money moved anyway."""
    run = new_run("ambiguous")
    demo.set_mode("slow-success", delay_ms=(ATTEMPT_TIMEOUT_SECONDS + 2) * 1000)
    burst = demo.burst("fallback", run, 8, 8)
    time.sleep(6)
    charged_while_pending = demo.stub_stats(run).get("charged", 0)
    pending = demo.order_counts(run).get("PaymentPending", 0)
    demo.set_mode("healthy")
    drained = wait_until(lambda: demo.order_counts(run).get("Paid", 0) == 8, timeout_s=180)
    stats = demo.stub_stats(run)
    failed = []
    if stats.get("charged") != 8:
        failed.append(f"expected exactly 8 charges, got {stats.get('charged')}")
    if drained is None:
        failed.append("orders did not settle")
    return {"orders": burst.summary(), "charged_while_orders_pending": charged_while_pending,
            "orders_pending_at_that_moment": pending, "provider": stats,
            "note": "duplicate = settlements that found the charge already made"}, failed


def recovery(demo: Demo) -> tuple[dict, list[str]]:
    """How long after the provider recovers does a caller get a paid order again?"""
    run = new_run("recovery")
    demo.set_mode("down")
    demo.burst("breaker", run, 60, 8)  # trip the breaker(s)
    demo.set_mode("healthy")
    started = time.perf_counter()
    first_paid = None
    rejected = 0
    while time.perf_counter() - started < 60:
        status, _, _ = demo.order("breaker", run)
        if status == 201:
            first_paid = time.perf_counter() - started
            break
        rejected += 1
        time.sleep(1)
    failed = [] if first_paid is not None else ["no paid order within 60 s of recovery"]
    return {"seconds_until_first_paid_order": None if first_paid is None else round(first_paid, 1),
            "rejected_while_provider_was_healthy": rejected,
            "break_seconds": BREAK_SECONDS}, failed


SCENARIOS = {
    "baseline": baseline,
    "naive": outage_naive,
    "breaker": outage_breaker,
    "gateway": outage_gateway,
    "fallback": fallback,
    "requeue": requeue,
    "ambiguous": ambiguous,
    "recovery": recovery,
}


# ---------- Entry point ----------

def azd_values() -> dict[str, str]:
    """Read KEY="value" lines from `azd env get-values`; empty when azd is unavailable."""
    try:
        output = subprocess.run("azd env get-values", shell=True, capture_output=True, text=True, timeout=60).stdout
    except (OSError, subprocess.SubprocessError):
        return {}
    values = {}
    for line in output.splitlines():
        key, separator, value = line.partition("=")
        if separator:
            values[key.strip()] = value.strip().strip('"')
    return values


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--orders-url")
    parser.add_argument("--payment-url")
    parser.add_argument("--only", help="comma-separated scenario names")
    parser.add_argument("--out", default="results/drive-results.json")
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args()

    if args.list:
        for name, scenario in SCENARIOS.items():
            print(f"{name:10} {scenario.__doc__}")
        return 0

    orders_url = args.orders_url or os.environ.get("ORDERS_URL")
    payment_url = args.payment_url or os.environ.get("PAYMENT_URL")
    if not orders_url or not payment_url:
        values = azd_values()
        orders_url = orders_url or values.get("ORDERS_URL")
        payment_url = payment_url or values.get("PAYMENT_URL")
    if not orders_url or not payment_url:
        print("Pass --orders-url and --payment-url, or run from the azd environment.", file=sys.stderr)
        return 2

    names = [n.strip() for n in args.only.split(",")] if args.only else list(SCENARIOS)
    unknown = [n for n in names if n not in SCENARIOS]
    if unknown:
        print(f"Unknown scenario(s): {', '.join(unknown)}", file=sys.stderr)
        return 2

    demo = Demo(orders_url, payment_url)
    results, all_failed = {}, []

    try:
        for name in names:
            print(f"\n=== {name}: {SCENARIOS[name].__doc__}")
            demo.recover()
            measurements, failed = SCENARIOS[name](demo)
            results[name] = {"measurements": measurements, "failed_checks": failed}
            print(json.dumps(measurements, indent=2))
            print("PASS" if not failed else "FAIL: " + "; ".join(failed))
            all_failed += [f"{name}: {f}" for f in failed]
    finally:
        # Never leave the provider broken, also after Ctrl+C.
        demo.set_mode("healthy")

    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(results, handle, indent=2)
    print(f"\nResults written to {args.out}")
    print("ALL PASSED" if not all_failed else f"{len(all_failed)} check(s) failed")
    return 0 if not all_failed else 1


if __name__ == "__main__":
    sys.exit(main())
