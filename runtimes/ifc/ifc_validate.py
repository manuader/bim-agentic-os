"""P8-08: openBIM schema QA of an IFC file with the approved IfcOpenShell validator.

Runs ifcopenshell.validate (schema: attribute types, optionality, aggregate bounds, inverse cardinalities,
GlobalId uniqueness; plus the EXPRESS where-rules and global rules it implements, express_rules=True) and prints
a deterministic JSON report (bim.ifc-validate/1) with typed issues. See docs/ifc.md.

  python ifc_validate.py INPUT_IFC [--output REPORT_JSON]

Exit codes: 0 valid (no issue at all), 1 invalid (at least one issue, including an unparseable STEP file),
2 usage error, unreadable input, unwritable/existing output or an internal validator failure.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys

import ifcopenshell
import ifcopenshell.validate

FORMAT = 'bim.ifc-validate/1'
MAX_BYTES = 256 * 1024 * 1024
MAX_MESSAGE = 4000
ISSUE_KINDS = (
    'syntax',               # the STEP file cannot be parsed into an IFC model
    'attribute_missing',    # a required (non-OPTIONAL) attribute is unset
    'attribute_invalid',    # wrong type, entity, enumeration or aggregate size for an attribute
    'inverse_cardinality',  # an INVERSE attribute outside its declared bounds
    'unique_rule',          # an EXPRESS UNIQUE rule (e.g. IfcRoot.UR1 GlobalId)
    'type_rule',            # a WHERE rule of a defined (simple) type, e.g. IfcPositiveLengthMeasure.WR1
    'where_rule',           # a WHERE rule of an entity
    'global_rule',          # an EXPRESS global RULE
    'header',               # STEP header issues reported by the validator
    'other',                # any other validator statement (validator_type keeps the raw type)
)
_RULE_TYPES = {'parse': 'syntax', 'entity_rule': 'where_rule', 'simpletype_rule': 'type_rule', 'global_rule': 'global_rule', 'header': 'header'}
_UNIQUE = re.compile(r'Rule\s+([A-Za-z0-9_]+\.[A-Za-z0-9_]+)\s*:')
_STEP_LINE = re.compile(r'\s*#(\d+)\s*=\s*([A-Za-z0-9_]+)')
# Parser statements logged while the validator reads the file itself (validate(path)); an opened model silently
# drops these instances, so validating an already opened model would miss them.
_PARSE = ('Entity with name', 'Instance reference', 'Expected ', 'Unable to parse', 'Syntax error', 'Unexpected token')


class ValidateError(Exception):
    def __init__(self, code, detail):
        super().__init__(f'{code}: {detail}')
        self.code = code
        self.detail = detail


def _text(value):
    if value is None:
        return None
    return str(value)


def _kind(statement):
    kind_type = statement.get('type')
    message = statement.get('message') or ''
    if kind_type == 'schema':
        if message.startswith('Unsupported schema'):
            return 'header'
        if not isinstance(statement.get('instance'), ifcopenshell.entity_instance) and message.startswith(_PARSE):
            return 'syntax'
        if message.startswith('Expected ') and ' attribute values, found ' in message:
            return 'syntax'
        if message.startswith('Attribute not optional'):
            return 'attribute_missing'
        if 'With inverse' in message:
            return 'inverse_cardinality'
        if _UNIQUE.search(message) and 'unique' in message:
            return 'unique_rule'
        if 'With attribute' in message:
            return 'attribute_invalid'
        return 'other'
    return _RULE_TYPES.get(kind_type, 'other')


def _issue(statement):
    kind = _kind(statement)
    message = (statement.get('message') or '').strip()
    attribute = _text(statement.get('attribute'))
    rule = None
    if kind in ('where_rule', 'type_rule', 'global_rule'):
        rule, attribute = attribute, None
    elif kind == 'unique_rule':
        match = _UNIQUE.search(message)
        rule = match.group(1) if match else None
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
    level = statement.get('level')
    return {
        'kind': kind,
        'level': 'warning' if level == 'warning' else 'error',
        'validator_type': _text(statement.get('type')),
        'rule': rule,
        'attribute': attribute,
        'entity': entity,
        'step_id': step_id,
        'global_id': global_id,
        'message': message[:MAX_MESSAGE],
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


def validate_file(path):
    """Validate one IFC file. Returns the report; never raises for bad input (status 'error' instead)."""
    path = Path(path)
    name = path.name
    try:
        if not path.is_file():
            raise ValidateError('ifc_unreadable', 'input is not a regular file')
        size = path.stat().st_size
        if size > MAX_BYTES:
            raise ValidateError('ifc_too_large', f'{size} bytes exceeds the {MAX_BYTES} byte cap')
        data = path.read_bytes()
    except ValidateError as error:
        return _error(name, error.code, error.detail)
    except OSError as error:
        return _error(name, 'ifc_unreadable', type(error).__name__)
    digest = hashlib.sha256(data).hexdigest()
    try:
        schema = ifcopenshell.open(str(path)).schema
    except Exception:
        schema = None
    logger = ifcopenshell.validate.json_logger()
    try:
        # The path form makes the validator parse the file itself and log parser errors (unknown entities,
        # dangling references, wrong attribute counts) before the schema and EXPRESS rule checks.
        ifcopenshell.validate.validate(str(path), logger, express_rules=True)
    except (ifcopenshell.Error, ifcopenshell.SchemaError, OSError) as error:
        detail = str(error).replace(str(path.resolve()), name).replace(str(path), name)
        logger.statements.append({'level': 'error', 'type': 'parse', 'message': f'{type(error).__name__}: {detail}'})
    except Exception as error:
        return _error(name, 'validator_failed', f'{type(error).__name__}: {error}'[:MAX_MESSAGE])
    issues = [_issue(s) for s in logger.statements]
    for issue in issues:  # The report names the file by basename only, never a local path.
        issue['message'] = issue['message'].replace(str(path.resolve()), name).replace(str(path), name)
    return _report(name, digest, len(data), schema, issues)


def _write_exclusive(target, text):
    target = Path(target)
    try:
        with target.open('x', encoding='utf-8', newline='\n') as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
    except FileExistsError:
        raise ValidateError('output_exists', 'the report file already exists; it was not replaced') from None
    except OSError as error:
        raise ValidateError('output_unwritable', type(error).__name__) from None


def dumps(report):
    return json.dumps(report, indent=2, sort_keys=False, allow_nan=False) + '\n'


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        print(json.dumps({'format': FORMAT, 'status': 'error', 'error': {'code': 'usage', 'detail': message}}))
        sys.exit(2)


def main(argv):
    parser = _Parser(prog='ifc_validate.py', description='Schema/EXPRESS validation of an IFC file (bim.ifc-validate/1).')
    parser.add_argument('input')
    parser.add_argument('--output')
    args = parser.parse_args(argv)
    try:
        report = validate_file(args.input)
    except Exception as error:  # One JSON line, never a traceback.
        report = _error(Path(args.input).name, 'internal_error', type(error).__name__)
    text = dumps(report)
    if args.output is not None:
        try:
            _write_exclusive(args.output, text)
        except ValidateError as error:
            print(json.dumps(_error(Path(args.input).name, error.code, error.detail)))
            return 2
    print(text, end='')
    return {'valid': 0, 'invalid': 1}.get(report['status'], 2)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
