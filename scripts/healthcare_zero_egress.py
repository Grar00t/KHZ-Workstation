from __future__ import annotations

import ipaddress
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import psutil

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / "acceptance" / "reports" / "healthcare-zero-egress.json"


def is_loopback(host: str) -> bool:
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return host.lower() == "localhost"


def observe(command: list[str], env: dict[str, str] | None = None) -> dict:
    proc = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
    unexpected = []
    seen = set()
    while proc.poll() is None:
        processes = []
        try:
            parent = psutil.Process(proc.pid)
            processes = [parent, *parent.children(recursive=True)]
        except psutil.Error:
            pass
        for p in processes:
            try:
                conns = p.net_connections(kind="inet")
            except (psutil.Error, PermissionError):
                continue
            for conn in conns:
                if not conn.raddr:
                    continue
                host = conn.raddr.ip if hasattr(conn.raddr, "ip") else conn.raddr[0]
                port = conn.raddr.port if hasattr(conn.raddr, "port") else conn.raddr[1]
                if is_loopback(host):
                    continue
                key = (p.pid, p.name(), host, port, conn.status)
                if key not in seen:
                    seen.add(key)
                    unexpected.append({"pid": p.pid, "process": p.name(), "remote_host": host, "remote_port": port, "status": conn.status})
        time.sleep(0.03)
    stdout, stderr = proc.communicate()
    return {"command": command, "exit_code": proc.returncode, "unexpected_non_loopback": unexpected, "stdout_tail": stdout[-3000:], "stderr_tail": stderr[-3000:]}


def main() -> int:
    env = os.environ.copy(); env["PYTHONPATH"] = str(ROOT / "src")
    scenarios = [
        observe([sys.executable, "scripts/no_ai_baseline.py"], env=env),
        observe(["/usr/bin/python3", "scripts/libreoffice_roundtrip.py"], env=env),
    ]
    unexpected = [x for s in scenarios for x in s["unexpected_non_loopback"]]
    status = "PASSED" if all(s["exit_code"] == 0 for s in scenarios) and not unexpected else "FAILED"
    report = {
        "scenario": "HEALTHCARE_ZERO_EGRESS",
        "platform": sys.platform,
        "settings": {"healthcare_hardened": True, "ai": False, "git_network": False, "updates": False, "network_policy": "LOOPBACK_ONLY"},
        "status": status,
        "unexpected_non_loopback": unexpected,
        "scenarios": scenarios,
        "scope_note": "Process connection observation only. This is not Windows Firewall proof and does not establish Windows 11 egress behavior.",
    }
    REPORT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps({"status": status, "unexpected_non_loopback": unexpected, "scenario_exit_codes": [s["exit_code"] for s in scenarios]}, indent=2))
    return 0 if status == "PASSED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
