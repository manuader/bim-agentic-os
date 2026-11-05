"""P8-08: openBIM schema QA of an IFC file with the approved IfcOpenShell validator.

Runs ifcopenshell.validate (schema: attribute types, optionality, aggregate bounds, inverse cardinalities,
GlobalId uniqueness; plus the EXPRESS where-rules and global rules it implements, express_rules=True) and prints
a deterministic JSON report (bim.ifc-validate/1) with typed issues. See docs/ifc.md.

  python ifc_validate.py INPUT_IFC [--output REPORT_JSON]

Exit codes: 0 valid (no issue at all), 1 invalid (at least one issue, including an empty or unparseable STEP file),
2 usage error, unreadable or oversized input, unwritable/existing output or a validator/environment failure.

The input is read once (at most MAX_BYTES + 1 bytes) into a private temporary copy with an ASCII name; that copy is
hashed and validated, so the reported hash is the validated content and a file changing underneath cannot mix
versions. Messages quote model content (names, GlobalIds): the report is QA evidence, not publish-safe.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile

import ifcopenshell
import ifcopenshell.validate

FORMAT = 'bim.ifc-validate/1'
MAX_BYTES = 256 * 1024 * 1024
MAX_MESSAGE = 4000
ISSUE_KINDS = (
    'syntax',               # empty file or STEP the parser cannot build (unknown entity, dangling reference, arity)
    'attribute_missing',    # a required (non-OPTIONAL) attribute is unset
    'attribute_invalid',    # wrong type, entity, enumeration or aggregate size for an attribute
    'inverse_cardinality',  # an INVERSE attribute outside its declared bounds
    'unique_rule',          # an EXPRESS UNIQUE rule (e.g. IfcRoot.UR1 GlobalId)
    'type_rule',            # a WHERE rule of a defined (simple) type, e.g. IfcPositiveLengthMeasure.WR1
    'where_rule',           # a WHERE rule of an entity
    'global_rule',          # an EXPRESS global RULE, e.g. IfcSingleProjectInstance
    'header',               # STEP header issues such as an unsupported schema
    'other',                # any other validator statement (validator_type keeps the raw type)
)
_RULE_TYPES = {'parse': 'syntax', 'entity_rule': 'where_rule', 'simpletype_rule': 'type_rule', 'global_rule': 'global_rule', 'header': 'header'}
_LEVELS = {'error': 'error', 'critical': 'error', 'exception': 'error', 'warning': 'warning', 'info': 'warning', 'debug': 'warning'}
# The validator writes 'Rule Entity.Name:' on a line of its own; model text (names) is quoted inside indented
# instance lines and cannot start a line, so anchoring at line start and end keeps it from posing as a rule.
_UNIQUE = re.compile(r'^Rule\s+([A-Za-z0-9_]+\.[A-Za-z0-9_]+):\s*$', re.M)
_STEP_LINE = re.compile(r'\s*#(\d+)\s*=\s*([A-Za-z0-9_]+)')
# Parser statements logged while the validator reads the file itself (validate(path)); an opened model silently
# drops these instances, so validating an already opened model would miss them.
_PARSE = ('Entity with name ', 'Instance reference ', 'Unable to parse', 'Syntax error', 'Unexpected token')
_ARITY = re.compile(r'^Expected \d+ attribute values, found \d+')


class ValidateError(Exception):
    def __init__(self, code, detail):
        super().__init__(f'{code}: {detail}')
        self.code = code
        self.detail = detail


def _text(value):
    return None if value is None else str(value)


def _kind(statement):
    kind_type = statement.get('type')
    message = statement.get('message') or ''
    if kind_type != 'schema':
        return _RULE_TYPES.get(kind_type, 'other')
    if message.startswith('Unsupported schema'):
        return 'header'
    if _ARITY.match(message):
        return 'syntax'
    if message.startswith(_PARSE) and not isinstance(statement.get('instance'), ifcopenshell.entity_instance):
        return 'syntax'
    if message.startswith('Attribute not optional'):
        return 'attribute_missing'
    if message.startswith('With inverse:'):
        return 'inverse_cardinality'
    if message.startswith('With attribute:'):
        return 'attribute_invalid'
    if message.startswith('On instance:') and _UNIQUE.search(message):
        return 'unique_rule'
    return 'other'


def _issue(statement, scrub):
    kind = _kind(statement)
    message = scrub((statement.get('message') or '').strip())[:MAX_MESSAGE]
    attribute = _text(statement.get('attribute'))
    rule = None
    if kind in ('where_rule', 'type_rule', 'global_rule'):
        rule, attribute = attribute, None
    elif kind == 'unique_rule':
        rule = _UNIQUE.search(message).group(1) if _UNIQUE.search(message) else None
    instance = statement.get('instance')
    entity = step_id = global_id = None
    if isinstance(instance, ifcopenshell.entity_instance):
        try:
            entity = instance.is_a()
            step_id = instance.id() or None
            if instance.is_a('IfcRoot'):
                global_id = instance.GlobalId or None
        except Exception:  # A broken instance still yields an issue, only less located.
            pass
    elif isinstance(instance, str):  # Raw STEP text of an instance the parser could not build.
        match = _STEP_LINE.match(instance)
        if match:
            step_id = int(match.group(1))
    return {
        'kind': kind,
        'level': _LEVELS.get(str(statement.get('level')).lower(), 'error'),
        'validator_type': _text(statement.get('type')),
        'rule': rule,
        'attribute': attribute,
        'entity': entity,
        'step_id': step_id,
        'global_id': global_id,
        'message': message,
    }


def _order(issue):
    return (issue['step_id'] if issue['step_id'] is not None else -1, issue['kind'], issue['rule'] or '', issue['attribute'] or '', issue['message'])


def _report(name, digest, size, schema, issues):
    issues = sorted(issues, key=_order)
    by_kind = {}
    for issue in issues:
        by_kind[issue['kind']] = by_kind.get(issue['kind'], 0) + 1
    errors = sum(1 for i in issues if i['level'] == 'error')
    return {
        'format': FORMAT,
        'status': 'valid' if not issues else 'invalid',
        'file': {'name': name, 'sha256': digest, 'bytes': size},
        'ifc_schema': schema,
        'validator': {'engine': 'ifcopenshell.validate', 'version': ifcopenshell.version, 'express_rules': True},
        'counts': {'issues': len(issues), 'errors': errors, 'warnings': len(issues) - errors, 'by_kind': dict(sorted(by_kind.items()))},
        'issues': issues,
    }


def _error(name, code, detail):
    return {'format': FORMAT, 'status': 'error', 'file': {'name': name}, 'error': {'code': code, 'detail': detail}}


def _scrubber(name, *paths):
    """Replace every spelling of the given local paths (and their directories) by the input basename or '<tmp>'."""
    pairs = []
    for path, replacement in paths:
        for spelling in {str(path), str(Path(path).resolve()), os.path.abspath(str(path))}:
            for variant in {spelling, spelling.replace('\\', '/'), spelling.replace('/', '\\')}:
                # Only absolute, non-root spellings: a relative '.' or a bare '/' would mangle every message.
                if len(variant) > 3 and (os.path.isabs(variant) or os.path.isabs(variant.replace('\\', '/'))):
                    pairs.append((variant, replacement))
    pairs.sort(key=lambda p: -len(p[0]))

    def scrub(text):
        for old, new in pairs:
            if old:
                text = text.replace(old, new)
        return text
    return scrub


def _read_capped(path):
    if not path.is_file():
        raise ValidateError('ifc_unreadable', 'input is not a regular file')
    try:
        with path.open('rb') as handle:
            data = handle.read(MAX_BYTES + 1)
    except OSError as error:
        raise ValidateError('ifc_unreadable', type(error).__name__) from None
    if len(data) > MAX_BYTES:
        raise ValidateError('ifc_too_large', f'more than {MAX_BYTES} bytes')
    return data


def validate_file(path):
    """Validate one IFC file. Returns the report; never raises for bad input (status 'error' instead)."""
    path = Path(path)
    name = path.name
    try:
        data = _read_capped(path)
    except ValidateError as error:
        return _error(name, error.code, error.detail)
    digest = hashlib.sha256(data).hexdigest()
    if not data:
        empty = {'level': 'error', 'type': 'parse', 'message': 'empty file: no STEP physical file content'}
        return _report(name, digest, 0, None, [_issue(empty, str)])
    private = tempfile.mkdtemp(prefix='bim-ifc-validate-')
    copy = Path(private) / 'input.ifc'
    scrub = _scrubber(name, (copy, name), (private, '<tmp>'), (path, name), (path.parent, '<dir>'))
    try:
        try:
            with copy.open('xb') as handle:
                handle.write(data)
        except OSError as error:
            return _error(name, 'validator_failed', f'private copy: {type(error).__name__}')
        try:
            schema = ifcopenshell.open(str(copy)).schema
        except Exception:
            schema = None
        logger = ifcopenshell.validate.json_logger()
        try:
            # The path form makes the validator parse the file itself and log parser errors (unknown entities,
            # dangling references, wrong attribute counts) before the schema and EXPRESS rule checks.
            ifcopenshell.validate.validate(str(copy), logger, express_rules=True)
        except (ifcopenshell.SchemaError, ifcopenshell.Error) as error:  # Content the parser refuses.
            logger.statements.append({'level': 'error', 'type': 'parse', 'message': f'{type(error).__name__}: {error}'})
        except Exception as error:  # Environment or validator failure: never reported as an invalid model.
            return _error(name, 'validator_failed', scrub(f'{type(error).__name__}: {error}')[:MAX_MESSAGE])
        return _report(name, digest, len(data), schema, [_issue(s, scrub) for s in logger.statements])
    finally:
        shutil.rmtree(private, ignore_errors=True)


def _write_exclusive(target, text):
    """Publish the report atomically: write a private pending file, then hard-link it (never replacing)."""
    target = Path(target)
    if target.exists() or target.is_symlink():
        raise ValidateError('output_exists', 'the report file already exists; it was not replaced')
    try:
        handle = tempfile.NamedTemporaryFile('w', encoding='utf-8', newline='\n', dir=str(target.parent or Path('.')),
                                             prefix='.' + target.name + '.', suffix='.pending', delete=False)
    except OSError as error:
        raise ValidateError('output_unwritable', type(error).__name__) from None
    pending = Path(handle.name)
    try:
        with handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        try:
            os.link(pending, target)
        except FileExistsError:
            raise ValidateError('output_exists', 'the report file already exists; it was not replaced') from None
        except OSError as error:
            raise ValidateError('output_unwritable', type(error).__name__) from None
    finally:
        try:
            pending.unlink()
        except OSError:
            pass


def dumps(report):
    return json.dumps(report, indent=2, allow_nan=False) + '\n'


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        print(json.dumps({'format': FORMAT, 'status': 'error', 'error': {'code': 'usage', 'detail': message}}))
        sys.exit(2)


def main(argv):
    parser = _Parser(prog='ifc_validate.py', description='Schema/EXPRESS validation of an IFC file (bim.ifc-validate/1).')
    parser.add_argument('input')
    parser.add_argument('--output')
    args = parser.parse_args(argv)
    name = Path(args.input).name
    try:
        report = validate_file(args.input)
    except Exception as error:  # One JSON document, never a traceback or a local path.
        report = _error(name, 'internal_error', type(error).__name__)
    text = dumps(report)
    if args.output is not None:
        try:
            _write_exclusive(args.output, text)
        except ValidateError as error:
            print(json.dumps(_error(name, error.code, error.detail)))
            return 2
    print(text, end='')
    return {'valid': 0, 'invalid': 1}.get(report['status'], 2)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
