from pathlib import Path
import hashlib
import json
import statistics
import subprocess

root = Path(__file__).resolve().parent
filters = 'theme-diag/base/mixed/1000,theme-diag/accent/mixed/1000,theme-diag/base/shadcn/1000,theme-diag/repeat-theme/mixed/1000'
manifests = json.loads((root / 'dll-manifests.json').read_text())['manifests']
reports = {'baseline': [], 'final': []}
order = [('baseline', 1), ('final', 1), ('final', 2), ('baseline', 2), ('baseline', 3), ('final', 3)]
for mode, block in order:
    runtime = root / 'frozen' / mode
    actual = {str(p.relative_to(runtime)): hashlib.sha256(p.read_bytes()).hexdigest() for p in runtime.rglob('*.dll')}
    assert actual == manifests[mode], f'{mode} runtime changed'
    output = root / 'paired' / f'{mode}-{block}'
    command = ['dotnet', str(runtime / 'SukiUI.PerformanceTests.dll'), '--filter', filters,
               '--warmup', '3', '--iterations', '7', '--repetitions', '1', '--seed', str(1729 + block),
               '--output', str(output)]
    print(f'Running {mode} block {block}', flush=True)
    subprocess.run(command, check=True)
    report = json.loads((output / 'results.json').read_text())
    assert len(report['Results']) == 4
    assert all(r['Error'] is None and len(r['Samples']) == 7 for r in report['Results'])
    assert report['Metadata']['SukiUISha256'].lower() == manifests[mode]['SukiUI.dll']
    for r in report['Results']:
        r['Replica'] = block
    reports[mode].append(report)

for mode, blocks in reports.items():
    combined = dict(blocks[0])
    combined['Finished'] = blocks[-1]['Finished']
    combined['Metadata'] = dict(combined['Metadata'], repetitions='3', blockOrder=str(order))
    combined['Results'] = [r for block in blocks for r in block['Results']]
    (root / f'paired-{mode}.json').write_text(json.dumps(combined, indent=2) + '\n')

comparison = []
for scenario in sorted({r['Id'] for block in reports['baseline'] for r in block['Results']}):
    row = {'id': scenario}
    for mode in reports:
        cases = [r for block in reports[mode] for r in block['Results'] if r['Id'] == scenario]
        medians = [statistics.median(s['Ms'] for s in r['Samples']) for r in cases]
        row[mode] = {'ms': statistics.median(medians), 'processMedians': medians,
                     'uiBytes': statistics.median(statistics.median(s['UiBytes'] for s in r['Samples']) for r in cases),
                     'events': sorted({s['Metrics']['applicationEvents'] for r in cases for s in r['Samples']}),
                     'colorCallbacks': sorted({s['Metrics']['themeEvents'] for r in cases for s in r['Samples']})}
    row['speedup'] = row['baseline']['ms'] / row['final']['ms']
    row['reductionPercent'] = 100 * (1 - row['final']['ms'] / row['baseline']['ms'])
    comparison.append(row)
(root / 'paired-comparison.json').write_text(json.dumps(comparison, indent=2) + '\n')
print(json.dumps(comparison, indent=2), flush=True)
