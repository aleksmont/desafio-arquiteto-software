"""Ensaio HTTP finito, sem dependências externas. Somente banco de avaliação."""
import concurrent.futures
import collections
import datetime
import decimal
import json
import math
import os
import pathlib
import platform
import re
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE = os.environ.get("BASE_URL", "http://api:8080").rstrip("/")
KEY = os.environ["API_KEY"]
COUNT = int(os.environ.get("LOAD_REQUESTS", "6000"))
if not 1 <= COUNT <= 100000:
    raise ValueError("LOAD_REQUESTS deve estar entre 1 e 100000")

def request(method, path, data=None):
    req = urllib.request.Request(BASE + path, method=method,
        headers={"X-Api-Key": KEY, "Content-Type": "application/json"},
        data=json.dumps(data).encode() if data is not None else None)
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            status, payload = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, payload = error.code, error.read()
    latency = (time.perf_counter() - started) * 1000
    return status, json.loads(payload, parse_float=decimal.Decimal) if payload else None, latency

def percentile(values, p):
    return round(sorted(values)[max(0, math.ceil(len(values) * p) - 1)], 3)

def scenario(name, accounts_count, workers):
    accounts = [str(uuid.uuid4()) for _ in range(accounts_count)]
    for account in accounts:
        assert request("PUT", "/accounts/" + account)[0] == 204
    jobs = [(accounts[i % accounts_count], {"key": str(uuid.uuid4()), "amount": 0.01, "type": "credit"}) for i in range(COUNT)]
    def submit(job):
        account, body = job
        return request("POST", f"/accounts/{account}/movements", body)
    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as executor:
        responses = list(executor.map(submit, jobs))
    elapsed = time.perf_counter() - started
    statuses = collections.Counter(status for status, _, _ in responses)
    assert set(statuses).issubset({201, 429, 503}), dict(statuses)
    latencies = [latency for _, _, latency in responses]
    # Recuperação fora da janela medida: todas as intenções usam sua chave original.
    expected = collections.Counter()
    historical_at = None
    recoveries = 0
    for (account, body), (first_status, original, _) in zip(jobs, responses):
        for attempt in range(6):
            status, recorded, _ = submit((account, body))
            if status in (200, 201):
                break
            assert status in (429, 503), status
            time.sleep(min(0.1 * 2 ** attempt, 2))
        else:
            raise AssertionError("Intenção não recuperada após tentativas limitadas")
        if first_status == 201:
            assert status == 200 and original["id"] == recorded["id"]
        else:
            recoveries += 1
        expected[account] += 1
        iso = recorded["recordedAt"].replace("Z", "+00:00")
        iso = re.sub(r"\.(\d+)(?=[+-])", lambda match: "." + match[1].ljust(6, "0")[:6], iso)
        instant = datetime.datetime.fromisoformat(iso)
        historical_at = max(historical_at, instant) if historical_at else instant
    for account in accounts:
        target = decimal.Decimal(expected[account]) / 100
        status, balance, _ = request("GET", f"/accounts/{account}/balance")
        assert status == 200 and balance["amount"] == target, balance
        at = urllib.parse.quote(historical_at.isoformat(), safe="")
        status, historical, _ = request("GET", f"/accounts/{account}/balance?at={at}")
        assert status == 200 and historical["amount"] == target, historical
    return {"name": name, "accounts": accounts_count, "workers": workers, "requests": COUNT,
        "duration_seconds": round(elapsed, 3), "http_responses_per_second": round(COUNT / elapsed, 2),
        "created_per_second": round(statuses[201] / elapsed, 2), "statuses": dict(statuses),
        "latency_ms_all_statuses": {"p50": percentile(latencies, .50), "p95": percentile(latencies, .95), "p99": percentile(latencies, .99)},
        "recovered_intentions": recoveries, "balance_and_history_validated": True,
        "expected_total_brl_after_recovery": str(decimal.Decimal(COUNT) / 100)}

def main():
    for _ in range(120):
        try:
            if request("GET", "/health/ready")[0] == 200:
                break
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(1)
    else:
        raise RuntimeError("API não ficou pronta")
    report = {"executed_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "generator": "Python urllib / HTTP real / novas conexões por requisição",
        "generator_platform": platform.platform(), "logical_cpus_visible": os.cpu_count(),
        "admission_limit_per_api_instance": 32,
        "measurement": "Lote finito fechado; sem warm-up explícito; recuperação fora da medição",
        "scenarios": []}
    for args in [("distributed", 32, 16), ("hot-account", 1, 16), ("over-admission-limit", 32, 96)]:
        result = scenario(*args)
        report["scenarios"].append(result)
        print(json.dumps(result), flush=True)
    output = pathlib.Path(os.environ.get("LOAD_REPORT", "/results/load.json"))
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n")
    print("Relatório: " + str(output), flush=True)

if __name__ == "__main__":
    main()
