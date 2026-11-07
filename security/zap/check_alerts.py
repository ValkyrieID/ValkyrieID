#!/usr/bin/env python3
"""Checks ZAP's JSON reports against the triage in rules.tsv. Standard library only.

Fails (exit 1) on:
- a Medium or High alert whose rule isn't listed in rules.tsv;
- an alert of any risk whose rule is listed as FAIL.
Prints every alert with its triage, and writes out/summary.md.

Usage: check_alerts.py <report.json>...
"""

import json
import os
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
RISKS = {0: 'Informational', 1: 'Low', 2: 'Medium', 3: 'High'}


def load_rules():
    """rules.tsv: <plugin id> TAB <IGNORE|WARN|FAIL> TAB <reason>; # starts a comment."""
    rules = {}
    for number, line in enumerate((HERE / 'rules.tsv').read_text().splitlines(), 1):
        if not line.strip() or line.startswith('#'):
            continue
        parts = line.split('\t')
        if len(parts) != 3 or parts[1] not in ('IGNORE', 'WARN', 'FAIL') or not parts[2].strip():
            sys.exit(f'rules.tsv:{number}: expected "<id>\\t<IGNORE|WARN|FAIL>\\t<reason>"')
        rules[parts[0]] = (parts[1], parts[2].strip())
    return rules


def main():
    rules = load_rules()
    problems, rows = [], []
    for report in sys.argv[1:]:
        scan = pathlib.Path(report).stem
        data = json.loads(pathlib.Path(report).read_text())
        for site in data.get('site', []):
            for alert in site.get('alerts', []):
                plugin, risk = str(alert['pluginid']), int(alert['riskcode'])
                action, reason = rules.get(plugin, (None, None))
                rows.append((scan, plugin, alert['name'], RISKS[risk], alert.get('count', len(alert.get('instances', []))), action or 'unlisted', reason or ''))
                if action == 'FAIL' or (action is None and risk >= 2):
                    problems.append(f"{scan}: {RISKS[risk]} alert {plugin} '{alert['name']}' is {'listed as FAIL' if action else 'not triaged in rules.tsv'}")

    lines = ['| Scan | Rule | Alert | Risk | Instances | Triage | Reason |', '|---|---|---|---|---|---|---|']
    lines += [f'| {s} | {p} | {n} | {r} | {c} | {a} | {why} |' for s, p, n, r, c, a, why in sorted(rows)]
    (HERE / 'out').mkdir(exist_ok=True)
    (HERE / 'out' / 'summary.md').write_text('\n'.join(lines) + '\n')
    print('\n'.join(lines))
    for p in problems:
        print('::error::' + p if os.environ.get('GITHUB_ACTIONS') else 'ERROR: ' + p)
    sys.exit(1 if problems else 0)


if __name__ == '__main__':
    main()
