"""P01: same +400 mm width correction on the real IFC worker, incremental versus full rebuild.

Incremental = the existing compiled plan path: Core Compiler.Diff -> IfcCompiler.Export packet ->
runtimes/ifc/worker.py resize_compiled on a published baseline IFC.
Full rebuild = the existing creation path (worker.create) regenerating the whole IFC from the
corrected Core project (the packet's AfterJson geometry, CommittedAfterJson revision), with the
same staged schema/compiled/geometry QA and atomic publish the incremental path runs inline.

The worker is imported read-only; its behaviour is not changed. Core diff/export runs once in
C# before sampling and is excluded from both timed scopes (both paths consume the same packet).
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import pathlib
import platform
import random
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[3]
_spec = importlib.util.spec_from_file_location('bim_ifc_worker_rebuild', ROOT / 'runtimes/ifc/worker.py')
w = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(w)

FILLINGS = ('door', 'window-north', 'window-east')
REL_TYPES = ('IfcRelAggregates', 'IfcRelContainedInSpatialStructure', 'IfcRelVoidsElement', 'IfcRelFillsElement',
             'IfcRelSpaceBoundary', 'IfcRelDefinesByProperties')
PRODUCT_TYPES = ('IfcWall', 'IfcDoor', 'IfcWindow', 'IfcSpace', 'IfcOpeningElement', 'IfcBuildingStorey', 'IfcSite', 'IfcBuilding')


def generate_packet(dotnet, target):
    """Run the existing C# Core -> IFC compiler harness (RoomCompiler, Compiler.Diff, IfcCompiler.Export)."""
    target = pathlib.Path(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([dotnet, 'run', '--project', str(ROOT / 'tests/ifc-compiler/Ifc.Compiler.Tests.csproj'),
                    '--configuration', 'Release', '--', str(target)], check=True, capture_output=True, text=True, timeout=300)
    return target


def corrected(compiled, packet):
    """Creation input for the corrected project: after-geometry as the authored state at the committed revision."""
    committed = json.loads(packet['CommittedAfterJson'])
    return dict(compiled, a=compiled['b'], before=committed, plan=dict(compiled['plan'], BeforeHash=compiled['committed_hash']))


def qa(path, compiled):
    """Same checks resize_compiled runs on its staged file before publication."""
    w.validate(w.ifcopenshell.open(str(path)))
    w.verify_compiled(path, compiled, True)
    w.inspect(path)


QA_FUNCTIONS = ('validate', 'verify_compiled', 'inspect')


class QaTimer:
    """Temporarily wraps the worker's QA functions with perf_counter accumulators; always restores them.

    Only outermost calls are counted (inspect() calls validate() internally), so nothing is double-counted.
    Read-only instrumentation of the worker module's globals; worker behaviour is unchanged.
    """

    def __enter__(self):
        self.original = {name: getattr(w, name) for name in QA_FUNCTIONS}
        self.ms = {name: 0.0 for name in QA_FUNCTIONS}
        self.calls = {name: 0 for name in QA_FUNCTIONS}
        self.depth = 0

        def wrap(name, function):
            def timed(*args, **kwargs):
                outermost = self.depth == 0
                self.depth += 1
                start = time.perf_counter()
                try:
                    return function(*args, **kwargs)
                finally:
                    self.depth -= 1
                    if outermost:
                        self.ms[name] += (time.perf_counter() - start) * 1000
                        self.calls[name] += 1
            return timed
        for name, function in self.original.items():
            setattr(w, name, wrap(name, function))
        return self

    def __exit__(self, *exc):
        for name, function in self.original.items():
            setattr(w, name, function)
        return False

    @property
    def total_ms(self):
        return sum(self.ms.values())


def run_incremental(directory, packet):
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True)
    path = directory / 'room.ifc'
    w.create(path, w.validate_request(packet))
    baseline = directory / 'before.ifc'
    baseline.write_bytes(path.read_bytes())
    with QaTimer() as timer:
        start = time.perf_counter()
        changed = w.resize_compiled(path, packet)
        ms = (time.perf_counter() - start) * 1000
    if any(getattr(w, name) is not timer.original[name] for name in QA_FUNCTIONS):
        raise RuntimeError('worker QA functions were not restored')
    if changed is not True:
        raise RuntimeError('incremental path did not publish a correction')
    retained = directory / 'compiled-request.json'
    return dict(path=path, baseline=baseline, ms=ms, qa_inline_ms=timer.total_ms, non_qa_ms=ms - timer.total_ms,
                qa_inline_breakdown_ms=dict(timer.ms), qa_inline_calls=dict(timer.calls),
                bytes_written=path.stat().st_size + retained.stat().st_size,
                ifc_bytes_written=path.stat().st_size, sidecar_bytes_written=retained.stat().st_size)


def run_rebuild(directory, packet):
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True)
    path = directory / 'room.ifc'
    # The model being replaced (untimed), so identity preservation is measured against it.
    w.create(path, w.validate_request(packet))
    baseline = directory / 'before.ifc'
    baseline.write_bytes(path.read_bytes())
    staging = path.with_suffix('.pending.ifc')
    start = time.perf_counter()
    compiled = w.validate_request(packet)
    w.create(staging, corrected(compiled, packet))
    authored = time.perf_counter()
    qa(staging, compiled)
    checked = time.perf_counter()
    os.replace(staging, path)
    done = time.perf_counter()
    author, qa_ms, publish = (authored - start) * 1000, (checked - authored) * 1000, (done - checked) * 1000
    return dict(path=path, baseline=baseline, author_ms=author, qa_ms=qa_ms, publish_ms=publish, non_qa_ms=author + publish,
                ms=(done - start) * 1000, bytes_written=path.stat().st_size, ifc_bytes_written=path.stat().st_size, sidecar_bytes_written=0)


def products(f):
    """Every IfcProduct keyed by logical role; openings keyed by the logical id of their filling."""
    e = w.entities(f)
    out = dict(e)
    for name in FILLINGS:
        out['opening-' + name] = e[name].FillsVoids[0].RelatingOpeningElement
    return out


def _close(a, b, tol):
    return len(a) == len(b) and all(abs(x - y) <= tol for x, y in zip(a, b))


def equivalence(incremental, rebuilt, packet):
    compiled = w.validate_request(packet)
    fi = w.ifcopenshell.open(str(incremental))
    fr = w.ifcopenshell.open(str(rebuilt))
    pi, pr = products(fi), products(fr)
    mismatches = []
    if set(pi) != set(pr):
        mismatches.append('product roles differ')
    for key in sorted(set(pi) & set(pr)):
        a, b = pi[key], pr[key]
        if a.is_a() != b.is_a():
            mismatches.append(key + ': type')
            continue
        if key == 'level':
            if a.Elevation != b.Elevation or tuple(a.ObjectPlacement.RelativePlacement.Location.Coordinates) != tuple(b.ObjectPlacement.RelativePlacement.Location.Coordinates):
                mismatches.append('level placement')
            continue
        if not _close(w.bounds(a), w.bounds(b), 1e-6):
            mismatches.append(key + ': bounds')
        va, ta = w.triangulate(a)
        vb, tb = w.triangulate(b)
        if abs(va - vb) > 1e-7 or ta != tb:
            mismatches.append(key + ': triangulated volume/faces')
        if a.is_a('IfcDoor') or a.is_a('IfcWindow'):
            if (a.OverallWidth, a.OverallHeight) != (b.OverallWidth, b.OverallHeight):
                mismatches.append(key + ': overall size')
    for kind in REL_TYPES + PRODUCT_TYPES:
        if len(fi.by_type(kind)) != len(fr.by_type(kind)):
            mismatches.append(kind + ': count')

    def strip(o):
        return dict(o, native_file=None, elements=[{k: v for k, v in e.items() if k != 'native_id'} for e in o['elements']])
    oi, orb = w.observe(incremental), w.observe(rebuilt)
    if strip(oi) != strip(orb):
        mismatches.append('observe() readback differs beyond native ids')
    for path, label in ((incremental, 'incremental'), (rebuilt, 'rebuild')):
        try:
            w.verify_compiled(path, compiled, True)
        except ValueError as exc:
            mismatches.append(label + ': ' + str(exc))
    si, sr = w.state(fi), w.state(fr)
    for key in ('revision', 'compiled_revision', 'compiled_hash', 'compiled_project'):
        if si.get(key) != sr.get(key):
            mismatches.append('state ' + key)
    return dict(equivalent=not mismatches, mismatches=mismatches, clear_width_mm=oi['clear_width_mm'],
                products_compared=len(pi), tolerance_mm=1e-6, volume_tolerance_m3=1e-7,
                state_difference='incremental carries the operation receipt; rebuild has receipts={} (no operation history)'
                if si.get('receipts') != sr.get('receipts') else None)


def identity_report(before, after):
    fb, fa = w.ifcopenshell.open(str(before)), w.ifcopenshell.open(str(after))
    rb_, ra = {e.GlobalId for e in fb.by_type('IfcRoot')}, {e.GlobalId for e in fa.by_type('IfcRoot')}
    pb, pa = products(fb), products(fa)
    preserved = sorted(k for k in pb if k in pa and pb[k].GlobalId == pa[k].GlobalId)
    return dict(root_total_before=len(rb_), root_total_after=len(ra), root_preserved=len(rb_ & ra),
                products_total=len(pb), products_preserved=len(preserved),
                products_changed_global_id=sorted(k for k in pb if k not in preserved))


def spf_diff(before, after):
    pattern = re.compile(r'^#(\d+)=(.*);\s*$')

    def lines(path):
        out = {}
        for line in pathlib.Path(path).read_text(encoding='utf-8', errors='replace').splitlines():
            m = pattern.match(line)
            if m:
                out[int(m.group(1))] = m.group(2)
        return out
    b, a = lines(before), lines(after)
    common = set(a) & set(b)
    return dict(instances_before=len(b), instances_after=len(a), unchanged=sum(a[i] == b[i] for i in common),
                changed=sum(a[i] != b[i] for i in common), added=len(set(a) - set(b)), removed=len(set(b) - set(a)))


def geometry_changed(before, after):
    pb, pa = products(w.ifcopenshell.open(str(before))), products(w.ifcopenshell.open(str(after)))
    return sorted(k for k in pb if k != 'level' and not _close(w.bounds(pb[k]), w.bounds(pa[k]), 1e-6))


def percentile(values, q):
    """Linear interpolation between closest ranks (numpy 'linear' default)."""
    s = sorted(values)
    k = (len(s) - 1) * q
    lo = int(k)
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def stats(values):
    return dict(n=len(values), p50=round(percentile(values, .5), 3), p95=round(percentile(values, .95), 3),
                min=round(min(values), 3), max=round(max(values), 3), mean=round(sum(values) / len(values), 3),
                samples=[round(v, 3) for v in values])


def environment(dotnet_version):
    cpu = None
    memory = None
    try:
        for line in pathlib.Path('/proc/cpuinfo').read_text().splitlines():
            if line.lower().startswith('model name'):
                cpu = line.split(':', 1)[1].strip()
                break
        for line in pathlib.Path('/proc/meminfo').read_text().splitlines():
            if line.startswith('MemTotal'):
                memory = line.split(':', 1)[1].strip()
                break
    except OSError:
        pass
    return dict(os=platform.platform(), system=platform.system(), release=platform.release(), machine=platform.machine(),
                cpu_model=cpu or platform.processor() or None, logical_cpus=os.cpu_count(), memory_total=memory,
                python=platform.python_version(), python_implementation=platform.python_implementation(),
                ifcopenshell=w.ifcopenshell.version, dotnet_sdk=dotnet_version,
                runner=dict(os=os.environ.get('RUNNER_OS'), arch=os.environ.get('RUNNER_ARCH'), name=os.environ.get('RUNNER_NAME'),
                            image=os.environ.get('ImageOS'), image_version=os.environ.get('ImageVersion')),
                github=dict(run_id=os.environ.get('GITHUB_RUN_ID'), run_attempt=os.environ.get('GITHUB_RUN_ATTEMPT'),
                            sha=os.environ.get('GITHUB_SHA'), ref=os.environ.get('GITHUB_REF_NAME'), job=os.environ.get('GITHUB_JOB')))


def sample_pair(base, index, packet, compiled):
    order = ('incremental', 'rebuild') if index % 2 == 0 else ('rebuild', 'incremental')
    runs = {}
    for name in order:
        runs[name] = (run_incremental if name == 'incremental' else run_rebuild)(base / f'{index}-{name}', packet)
    inc, reb = runs['incremental'], runs['rebuild']
    start = time.perf_counter()
    qa(inc['path'], compiled)
    posthoc = (time.perf_counter() - start) * 1000
    proof = equivalence(inc['path'], reb['path'], packet)
    if not proof['equivalent']:
        raise RuntimeError('paths diverged on sample %d: %s' % (index, proof['mismatches']))
    return order, inc, reb, posthoc, proof


def bootstrap_p50_difference(treatment, control, resamples=10000, seed=20261023):
    """Percentile bootstrap 95% CI of p50(treatment) - p50(control); paths resampled independently, fixed seed."""
    rng = random.Random(seed)
    diffs = sorted(percentile([rng.choice(treatment) for _ in treatment], .5) - percentile([rng.choice(control) for _ in control], .5)
                   for _ in range(resamples))
    return dict(low=round(percentile(diffs, .025), 3), high=round(percentile(diffs, .975), 3), resamples=resamples, seed=seed,
                method='percentile bootstrap, independent resampling with replacement of each path')


def compare(name, treatment, control, treatment_label, control_label):
    diff = percentile(treatment, .5) - percentile(control, .5)
    ci = bootstrap_p50_difference(treatment, control)
    spread = dict(treatment_max_minus_min=round(max(treatment) - min(treatment), 3), control_max_minus_min=round(max(control) - min(control), 3))
    includes_zero = ci['low'] <= 0 <= ci['high']
    smaller_than_spread = abs(diff) < min(spread.values())
    if includes_zero:
        verdict = 'no measurable difference: the 95% CI includes 0'
    else:
        verdict = '%s is %s than %s at p50 (95%% CI excludes 0)' % (treatment_label, 'slower' if diff > 0 else 'faster', control_label)
    return dict(comparison=name, treatment=treatment_label, control=control_label, p50_difference_ms=round(diff, 3),
                p50_ratio=round(percentile(treatment, .5) / percentile(control, .5), 4), bootstrap_95ci_ms=ci,
                ci_includes_zero=includes_zero, within_path_spread_ms=spread, difference_smaller_than_within_path_spread=smaller_than_spread,
                statement='%s; p50 difference %.3f ms [%.3f, %.3f], %s the within-path max-min spread (%.3f / %.3f ms).' % (
                    verdict, diff, ci['low'], ci['high'], 'smaller than' if smaller_than_spread else 'not smaller than',
                    spread['treatment_max_minus_min'], spread['control_max_minus_min']))


def notice(report, limit=3000):
    """GitHub annotations are the only channel back from CI; split the one-line JSON into ordered parts."""
    raw = json.dumps(report, separators=(',', ':'), sort_keys=True)
    parts = [raw[i:i + limit] for i in range(0, len(raw), limit)]
    for i, part in enumerate(parts, 1):
        escaped = part.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
        print(f'::notice title=rebuild-benchmark {i}/{len(parts)}::{escaped}', flush=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--packet', required=True, help='Core compiled packet (tests/ifc-compiler output, resize-400)')
    parser.add_argument('--samples', type=int, default=10)
    parser.add_argument('--warmup', type=int, default=1)
    parser.add_argument('--output', required=True)
    parser.add_argument('--dotnet-version')
    parser.add_argument('--command', help='exact invoking command line, recorded verbatim')
    parser.add_argument('--notice', action='store_true')
    args = parser.parse_args(argv)
    if args.samples < 10:
        parser.error('P01 requires N>=10 samples per path')
    packet_bytes = pathlib.Path(args.packet).read_bytes()
    packet = json.loads(packet_bytes.decode('utf-8-sig'))
    compiled = w.validate_request(packet)
    if (compiled['a']['width'], compiled['b']['width']) != (5000, 5400):
        raise SystemExit('packet is not the +400 mm width correction 5000 -> 5400 mm')
    series = {k: [] for k in ('incremental_ms', 'incremental_qa_inline_ms', 'incremental_non_qa_ms', 'rebuild_ms', 'rebuild_author_ms',
                              'rebuild_qa_ms', 'rebuild_publish_ms', 'rebuild_non_qa_ms', 'incremental_qa_posthoc_crosscheck_ms')}
    breakdown = {name: [] for name in QA_FUNCTIONS}
    calls = None
    orders = []
    with tempfile.TemporaryDirectory(prefix='bim-rebuild-') as tmp:
        base = pathlib.Path(tmp)
        warm = [sample_pair(base, -1 - i, packet, compiled) for i in range(args.warmup)]
        for i in range(args.samples):
            order, inc, reb, qa_ms, proof = sample_pair(base, i, packet, compiled)
            orders.append(order[0])
            for key, value in (('incremental_ms', inc['ms']), ('incremental_qa_inline_ms', inc['qa_inline_ms']),
                               ('incremental_non_qa_ms', inc['non_qa_ms']), ('rebuild_ms', reb['ms']), ('rebuild_author_ms', reb['author_ms']),
                               ('rebuild_qa_ms', reb['qa_ms']), ('rebuild_publish_ms', reb['publish_ms']), ('rebuild_non_qa_ms', reb['non_qa_ms']),
                               ('incremental_qa_posthoc_crosscheck_ms', qa_ms)):
                series[key].append(value)
            for name in QA_FUNCTIONS:
                breakdown[name].append(inc['qa_inline_breakdown_ms'][name])
            calls = inc['qa_inline_calls']
        # Structural evidence from the last pair (deterministic across samples apart from fresh rebuild GlobalIds).
        model = dict(
            baseline_ifc_bytes=inc['baseline'].stat().st_size,
            incremental=dict(final_ifc_bytes=inc['path'].stat().st_size, bytes_written=inc['bytes_written'],
                             ifc_bytes_written=inc['ifc_bytes_written'], sidecar_bytes_written=inc['sidecar_bytes_written'],
                             sha256=hashlib.sha256(inc['path'].read_bytes()).hexdigest(),
                             products_geometry_changed=geometry_changed(inc['baseline'], inc['path']),
                             spf_instances=spf_diff(inc['baseline'], inc['path']), identity=identity_report(inc['baseline'], inc['path'])),
            rebuild=dict(final_ifc_bytes=reb['path'].stat().st_size, bytes_written=reb['bytes_written'],
                         ifc_bytes_written=reb['ifc_bytes_written'], sidecar_bytes_written=0,
                         sha256=hashlib.sha256(reb['path'].read_bytes()).hexdigest(),
                         products_geometry_changed=geometry_changed(reb['baseline'], reb['path']),
                         products_authored=len(products(w.ifcopenshell.open(str(reb['path'])))),
                         spf_instances=spf_diff(reb['baseline'], reb['path']), identity=identity_report(reb['baseline'], reb['path'])),
            equivalence=proof)
        warmup = dict(incremental_ms=[round(x[1]['ms'], 3) for x in warm], rebuild_ms=[round(x[2]['ms'], 3) for x in warm])
    total = compare('end-to-end, same QA in both', series['rebuild_ms'], series['incremental_ms'], 'full rebuild', 'incremental')
    non_qa = compare('like-for-like excluding QA', series['rebuild_non_qa_ms'], series['incremental_non_qa_ms'],
                     'rebuild author+publish', 'incremental minus inline QA')
    share = dict(incremental=round(percentile([q / t for q, t in zip(series['incremental_qa_inline_ms'], series['incremental_ms'])], .5), 4),
                 rebuild=round(percentile([q / t for q, t in zip(series['rebuild_qa_ms'], series['rebuild_ms'])], .5), 4))
    interpretation = dict(
        end_to_end=total, excluding_qa=non_qa, qa_share_of_path_p50=share,
        qa_dominates_both_paths=min(share.values()) > .5,
        statement='QA (schema validate + verify_compiled + inspect) is %.1f%% of incremental and %.1f%% of rebuild time at p50%s. %s %s' % (
            share['incremental'] * 100, share['rebuild'] * 100, ', so it dominates both paths' if min(share.values()) > .5 else '',
            'End-to-end: ' + total['statement'], 'Excluding QA: ' + non_qa['statement']))
    report = dict(
        schema='bim.ifc-rebuild-benchmark/1', task='P01',
        generated_utc=datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        command=args.command or ' '.join([sys.executable, *sys.argv]),
        correction=dict(operation_id=packet['OperationId'], width_before_mm=compiled['a']['width'], width_after_mm=compiled['b']['width'],
                        core_plan_operations=len(compiled['plan']['Operations']), core_dirty_ids=len(compiled['plan']['DirtyIds']),
                        packet_sha256=hashlib.sha256(packet_bytes).hexdigest()),
        environment=environment(args.dotnet_version),
        method=dict(samples_per_path=args.samples, warmup_pairs_discarded=args.warmup, percentile='linear interpolation between closest ranks',
                    order='paths alternate first position per sample', first_in_pair=orders, clock='time.perf_counter, in-process',
                    fresh_directory_per_sample=True, equivalence_checked_every_sample=True),
        scopes=dict(
            incremental_ms='worker.resize_compiled(path, packet) on a published baseline: open IFC, validate packet, verify before-state, '
                           'mutate 5 products + 1 opening in place, set state/receipt, retain packet sidecar, write staging IFC, '
                           'schema validate, verify_compiled, inspect (triangulate), atomic replace',
            incremental_qa_inline_ms='time inside incremental_ms spent in outermost calls of worker validate/verify_compiled/inspect, '
                                     'measured by temporarily wrapping those functions with perf_counter accumulators (restored after)',
            incremental_non_qa_ms='incremental_ms - incremental_qa_inline_ms: open, packet validation, state checks, mutation, '
                                  'packet sidecar, staging write, atomic replace',
            rebuild_ms='validate packet + worker.create(staging) from the corrected project + the same staged QA '
                       '(schema validate, verify_compiled, inspect) + atomic replace; no packet sidecar',
            rebuild_author_ms='validate packet + worker.create(staging): authoring and the single IFC write',
            rebuild_qa_ms='validate + verify_compiled + inspect on the staged rebuild',
            rebuild_publish_ms='atomic os.replace of the staged rebuild',
            rebuild_non_qa_ms='rebuild_author_ms + rebuild_publish_ms (like-for-like with incremental_non_qa_ms)',
            incremental_qa_posthoc_crosscheck_ms='cross-check only: the same QA re-run separately on the incremental output after the '
                                                 'sample; not part of incremental_ms',
            excluded='C# Core RoomCompiler/Compiler.Diff/IfcCompiler.Export (run once before sampling, shared by both paths), '
                     'baseline creation, process start, Python import, interpreter warmup, Revit, documentation'),
        timings=dict(**{k: stats(v) for k, v in series.items()}, incremental_qa_inline_breakdown_ms={k: stats(v) for k, v in breakdown.items()},
                     incremental_qa_inline_outermost_calls=calls, warmup=warmup),
        interpretation=interpretation,
        model=model,
        claim_scope='Single 9-logical-product IFC4 room fixture (12 IfcProducts), one +400 mm width correction, isolated IfcOpenShell worker '
                    'in one Python process on the recorded runner. Says nothing about larger models, Revit, documentation, or end-to-end '
                    'agent latency. Both paths serialize the whole IFC-SPF file; "incremental" means in-place mutation of existing '
                    'entities with stable GlobalIds, not a partial file write.')
    out = pathlib.Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps(dict(incremental_p50=report['timings']['incremental_ms']['p50'], rebuild_p50=report['timings']['rebuild_ms']['p50'],
                          statement=interpretation['statement'], equivalent=proof['equivalent'])))
    if args.notice:
        notice(report)
    return 0


if __name__ == '__main__':
    sys.exit(main())
