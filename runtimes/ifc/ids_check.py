"""Deterministic checker for a documented subset of buildingSMART IDS 1.0.

Supported: entity (name, predefinedType; one per applicability), attribute,
property (propertySet, baseName, value, dataType) and classification (value,
system) facets; ids:simpleValue, xs:enumeration and xs:pattern restrictions on
xs:string or numeric bases; SI unit conversion for length/area/volume
properties; specification cardinality required/optional/prohibited and facet
cardinality required/optional/prohibited. Everything else fails closed with a
stable error code (exit 2) instead of passing silently. See docs/ifc.md.

Usage: python ids_check.py SPEC.ids MODEL.ifc [--output report.json]
Exit codes: 0 pass, 1 fail, 2 invalid/unsupported IDS or unreadable IFC.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

REPORT_SCHEMA = 'bim.ids-report/1'
IDS_NS = 'http://standards.buildingsmart.org/IDS'
XS_NS = 'http://www.w3.org/2001/XMLSchema'
MAX_IDS_BYTES = 4 * 1024 * 1024
SUPPORTED_FACETS = ('entity', 'attribute', 'property', 'classification')
KNOWN_UNSUPPORTED_FACETS = ('partOf', 'material')
CARDINALITIES = ('required', 'optional', 'prohibited')
IFC_VERSIONS = ('IFC2X3', 'IFC4', 'IFC4X3_ADD2')
SPECIFICATION_ATTRIBUTES = {'name', 'ifcVersion', 'identifier', 'description', 'instructions'}
_XML_ENCODING = re.compile(r'^\s*<\?xml[^>]*\bencoding\s*=\s*["\']([^"\']+)["\']')


def normalise_schema(schema):
    """Map an IfcOpenShell schema identifier to the IDS ifcVersion token."""
    schema = str(schema).upper()
    return 'IFC4X3_ADD2' if schema in ('IFC4X3', 'IFC4X3_ADD2') else schema


class IdsError(Exception):
    """Invalid or unsupported IDS/IFC input; `code` is stable for callers."""

    def __init__(self, code, message):
        super().__init__(f'{code}: {message}')
        self.code = code
        self.message = message


def _ids(tag):
    return f'{{{IDS_NS}}}{tag}'


def _xs(tag):
    return f'{{{XS_NS}}}{tag}'


def _local(element):
    tag = element.tag
    return tag.split('}', 1)[1] if tag.startswith('{') else tag


def _children(element):
    return list(element)


# --------------------------------------------------------------------------- restrictions

MAX_VALUE_CHARS = 4096
_DECIMAL = r'[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)'
_LITERAL = {
    'xs:integer': re.compile(r'[+-]?[0-9]+'),
    'xs:decimal': re.compile(_DECIMAL),
    'xs:double': re.compile(_DECIMAL + r'([eE][+-]?[0-9]+)?'),
    'xs:float': re.compile(_DECIMAL + r'([eE][+-]?[0-9]+)?'),
}
NUMERIC_BASES = tuple(_LITERAL)
SUPPORTED_BASES = ('xs:string',) + NUMERIC_BASES
_NUMBER = _LITERAL['xs:double']
# XSD escapes copied verbatim; \s, \S and '.' are translated, everything else is refused.
_VERBATIM_ESCAPES = set('nrt\\|.-^?*+{}()[]dD')
_XSD_SPACE = ' \\t\\n\\r'


def _normalise(actual):
    """Return a comparable scalar or None (absent/non-scalar)."""
    if actual is None:
        return None
    if hasattr(actual, 'wrappedValue'):
        actual = actual.wrappedValue
    if isinstance(actual, bool):
        return 'TRUE' if actual else 'FALSE'
    if isinstance(actual, (int, float, str)):
        if isinstance(actual, str) and actual == '':
            return None
        if isinstance(actual, float) and not math.isfinite(actual):
            return None
        return actual
    return None


def _is_number(actual):
    return isinstance(actual, (int, float)) and not isinstance(actual, bool)


def _num_equal(expected, actual):
    return abs(float(actual) - expected) <= 1e-6 * max(1.0, abs(expected))


def _as_text(actual):
    if isinstance(actual, float) and actual.is_integer():
        return str(int(actual))
    return str(actual)


class Restriction:
    """One IDS value constraint: simpleValue, enumeration or pattern (alternatives)."""

    def __init__(self, kind, values, regex=None, base=None):
        self.kind = kind
        self.values = tuple(values)
        self.base = base
        self._regex = regex
        self._numbers = tuple(float(v) for v in values) if base in NUMERIC_BASES else None

    def matches(self, actual):
        actual = _normalise(actual)
        if actual is None:
            return False
        if isinstance(actual, str) and len(actual) > MAX_VALUE_CHARS:
            raise IdsError('IFC_VALUE_TOO_LONG', f'value longer than {MAX_VALUE_CHARS} characters')
        if self.kind == 'pattern':
            # Sibling xs:pattern facets are alternatives (XSD 1.0 section 4.3.4.3).
            return isinstance(actual, str) and any(r.fullmatch(actual) is not None for r in self._regex)
        if self.base in NUMERIC_BASES:
            if not _is_number(actual):
                return False
            if self.base == 'xs:integer' and float(actual) != int(actual):
                return False
            return any(_num_equal(n, actual) for n in self._numbers)
        if self.base == 'xs:string':
            return isinstance(actual, str) and actual in self.values
        # simpleValue: numeric literal compares numerically with numeric values, otherwise exact text.
        if _is_number(actual):
            return any(_NUMBER.fullmatch(v) is not None and _num_equal(float(v), actual) for v in self.values)
        return actual in self.values

    def describe(self):
        if self.kind == 'simpleValue':
            return repr(self.values[0])
        if self.kind == 'enumeration':
            return 'one of ' + ', '.join(repr(v) for v in self.values)
        return 'pattern ' + ' or '.join(repr(v) for v in self.values)


def _unsupported(pattern, why):
    return IdsError('IDS_UNSUPPORTED_RESTRICTION', f'pattern {pattern!r}: {why}')


def _translate_pattern(pattern):
    """Translate the supported XSD regex subset to Python, refusing divergent or ReDoS-prone syntax."""
    out = []
    groups = []  # per open group: does it contain a repeating quantifier?
    in_class = False
    after_group_repeat = False  # previous atom was a group that repeats internally
    after_quantifier = False
    i = 0
    n = len(pattern)
    while i < n:
        c = pattern[i]
        if c == '\\':
            if i + 1 >= n:
                raise IdsError('IDS_INVALID', f'dangling escape in pattern {pattern!r}')
            e = pattern[i + 1]
            if e == 's':
                out.append(_XSD_SPACE if in_class else f'[{_XSD_SPACE}]')
            elif e == 'S' and not in_class:
                out.append(f'[^{_XSD_SPACE}]')
            elif e in _VERBATIM_ESCAPES:
                out.append('\\' + e)
            else:
                raise _unsupported(pattern, f'escape \\{e} is not supported')
            i += 2
            after_group_repeat = after_quantifier = False
            continue
        if in_class:
            if c == '[':
                raise _unsupported(pattern, 'nested class or class subtraction is not supported')
            if c == ']':
                in_class = False
            out.append(c)
            i += 1
            continue
        repeat = c in '*+{'
        if repeat or c == '?':
            if after_quantifier:
                raise _unsupported(pattern, 'stacked or lazy quantifiers are not XSD')
            if c == '{':
                close = pattern.find('}', i)
                if close < 0 or not re.fullmatch(r'[0-9]+(,[0-9]*)?', pattern[i + 1:close]):
                    raise IdsError('IDS_INVALID', f'invalid quantifier in pattern {pattern!r}')
                out.append(pattern[i:close + 1])
                i = close + 1
            else:
                out.append(c)
                i += 1
            if repeat and after_group_repeat:
                raise _unsupported(pattern, 'nested repeating quantifiers risk catastrophic backtracking')
            if repeat and groups:
                groups[-1] = True
            after_quantifier = True
            after_group_repeat = False
            continue
        after_quantifier = False
        after_group_repeat = False
        if c == '[':
            in_class = True
            out.append(c)
            if pattern[i + 1:i + 2] == '^':
                out.append('^')
                i += 1
        elif c in '^$':
            raise _unsupported(pattern, f'unescaped {c!r} is literal in XSD but an anchor in Python')
        elif c == '.':
            out.append('[^\\n\\r]')
        elif c == '(':
            if pattern[i + 1:i + 2] == '?':
                raise _unsupported(pattern, 'non-XSD group syntax')
            groups.append(False)
            out.append(c)
        elif c == ')':
            if not groups:
                raise IdsError('IDS_INVALID', f'unbalanced parenthesis in pattern {pattern!r}')
            inner = groups.pop()
            if inner and groups:
                groups[-1] = True
            after_group_repeat = inner
            out.append(c)
        else:
            out.append(c)
        i += 1
    if in_class or groups:
        raise IdsError('IDS_INVALID', f'unbalanced pattern {pattern!r}')
    try:
        return re.compile(''.join(out))
    except re.error as exc:
        raise IdsError('IDS_INVALID', f'invalid pattern {pattern!r}: {exc}') from None


def parse_value(element):
    """Parse an IDS idsValue element (e.g. ids:name, ids:value) into a Restriction."""
    children = _children(element)
    if len(children) != 1:
        raise IdsError('IDS_INVALID', f'{_local(element)} must contain exactly one simpleValue or xs:restriction')
    child = children[0]
    if child.tag == _ids('simpleValue'):
        if len(child) or child.text is None or child.text.strip() == '':
            raise IdsError('IDS_INVALID', f'empty simpleValue in {_local(element)}')
        return Restriction('simpleValue', [child.text.strip()])
    if child.tag != _xs('restriction'):
        raise IdsError('IDS_INVALID', f'unexpected <{_local(child)}> in {_local(element)}')
    base = child.get('base')
    if not base:
        raise IdsError('IDS_INVALID', f'xs:restriction without base in {_local(element)}')
    if base not in SUPPORTED_BASES:
        raise IdsError('IDS_UNSUPPORTED_RESTRICTION', f'xs:restriction base {base!r} is not supported (use one of {list(SUPPORTED_BASES)})')
    facets = _children(child)
    if not facets:
        raise IdsError('IDS_INVALID', f'empty xs:restriction in {_local(element)}')
    kinds = {_local(f) if f.tag.startswith(f'{{{XS_NS}}}') else f.tag for f in facets}
    unsupported = sorted(k for k in kinds if k not in ('enumeration', 'pattern'))
    if unsupported:
        raise IdsError('IDS_UNSUPPORTED_RESTRICTION', f'xs:restriction facets {unsupported} are not supported (only xs:enumeration or xs:pattern)')
    if len(kinds) != 1:
        raise IdsError('IDS_UNSUPPORTED_RESTRICTION', 'mixing xs:enumeration and xs:pattern in one restriction is not supported')
    values = []
    for facet in facets:
        v = facet.get('value')
        if v is None:
            raise IdsError('IDS_INVALID', f'xs:{_local(facet)} without value attribute')
        values.append(v)
    kind = kinds.pop()
    if kind == 'pattern':
        if base != 'xs:string':
            raise IdsError('IDS_UNSUPPORTED_RESTRICTION', f'xs:pattern is only supported with base xs:string, not {base!r}')
        return Restriction('pattern', values, [_translate_pattern(v) for v in values], base)
    if base in NUMERIC_BASES:
        bad = [v for v in values if _LITERAL[base].fullmatch(v.strip()) is None]
        if bad:
            raise IdsError('IDS_INVALID', f'{bad} are not valid {base} literals')
        values = [v.strip() for v in values]
    return Restriction('enumeration', values, base=base)


# --------------------------------------------------------------------------- IDS model

class Facet:
    def __init__(self, kind, cardinality, parts, data_type=None):
        self.kind = kind
        self.cardinality = cardinality
        self.parts = parts
        self.data_type = data_type


class Specification:
    def __init__(self, name, identifier, versions, cardinality, applicability, requirements):
        self.name = name
        self.identifier = identifier
        self.ifc_versions = versions
        self.cardinality = cardinality
        self.applicability = applicability
        self.requirements = requirements


class IdsDocument:
    def __init__(self, title, specifications):
        self.title = title
        self.specifications = specifications


_FACET_PARTS = {
    'entity': (('name',), ('predefinedType',)),
    'attribute': (('name',), ('value',)),
    'property': (('propertySet', 'baseName'), ('value',)),
    'classification': ((), ('value', 'system')),
}
_FACET_ATTRIBUTES = {
    'entity': set(),
    'attribute': {'cardinality'},
    'property': {'cardinality', 'dataType', 'uri'},
    'classification': {'cardinality', 'uri'},
}


def _parse_facet(element, in_requirements):
    kind = _local(element) if element.tag.startswith(f'{{{IDS_NS}}}') else None
    if kind in KNOWN_UNSUPPORTED_FACETS:
        raise IdsError('IDS_UNSUPPORTED_FACET', f'IDS facet <{kind}> is not supported by this checker')
    if kind not in SUPPORTED_FACETS:
        raise IdsError('IDS_INVALID', f'unexpected element <{_local(element)}> where a facet was expected')
    allowed = set(_FACET_ATTRIBUTES[kind]) | {'instructions'}
    if not in_requirements:
        allowed -= {'cardinality'}
    extra = sorted(set(element.attrib) - allowed)
    if extra:
        raise IdsError('IDS_UNSUPPORTED_ATTRIBUTE', f'<{kind}> attributes {extra} are not supported here')
    cardinality = element.get('cardinality', 'required')
    if cardinality not in CARDINALITIES:
        raise IdsError('IDS_INVALID', f'<{kind}> cardinality {cardinality!r} is not one of {CARDINALITIES}')
    required, optional = _FACET_PARTS[kind]
    parts = {}
    for child in _children(element):
        name = _local(child)
        if child.tag != _ids(name) or name not in required + optional or name in parts:
            raise IdsError('IDS_INVALID', f'unexpected or duplicate <{name}> in <{kind}>')
        parts[name] = parse_value(child)
    missing = [p for p in required if p not in parts]
    if missing:
        raise IdsError('IDS_INVALID', f'<{kind}> is missing {missing}')
    if kind == 'entity' and parts['name'].kind == 'pattern':
        raise IdsError('IDS_UNSUPPORTED_RESTRICTION', 'entity name patterns are not supported (use simpleValue or enumeration)')
    if kind == 'attribute' and parts['name'].kind == 'pattern':
        raise IdsError('IDS_UNSUPPORTED_RESTRICTION', 'attribute name patterns are not supported (use simpleValue or enumeration)')
    if kind == 'entity':
        parts['name'] = Restriction(parts['name'].kind, [v.upper() for v in parts['name'].values])
    data_type = element.get('dataType')
    return Facet(kind, cardinality, parts, data_type.upper() if data_type else None)


def _occurs(element):
    low = element.get('minOccurs', '1')
    high = element.get('maxOccurs', 'unbounded')
    mapping = {('1', 'unbounded'): 'required', ('0', 'unbounded'): 'optional', ('0', '0'): 'prohibited'}
    if (low, high) not in mapping:
        raise IdsError('IDS_UNSUPPORTED_CARDINALITY', f'applicability minOccurs={low!r} maxOccurs={high!r} is not supported')
    return mapping[(low, high)]


def parse_ids(text):
    """Parse IDS XML text into an IdsDocument or raise IdsError."""
    if isinstance(text, bytes):
        raw = text
        try:
            text = text.decode('utf-8-sig')
        except UnicodeDecodeError:
            raise IdsError('IDS_XML_MALFORMED', 'IDS must be UTF-8') from None
    else:
        raw = text.encode('utf-8')
    if len(raw) > MAX_IDS_BYTES:
        raise IdsError('IDS_INVALID', f'IDS larger than {MAX_IDS_BYTES} bytes')
    declared = _XML_ENCODING.match(text)
    if declared and declared.group(1).strip().upper().replace('_', '-') not in ('UTF-8', 'UTF8'):
        raise IdsError('IDS_XML_MALFORMED', f'IDS must be UTF-8, not {declared.group(1)!r}')
    upper = text.upper()
    if '<!DOCTYPE' in upper or '<!ENTITY' in upper:
        raise IdsError('IDS_DOCTYPE_FORBIDDEN', 'DOCTYPE/ENTITY declarations are refused')
    try:
        root = ET.fromstring(text)
    except ET.ParseError as exc:
        raise IdsError('IDS_XML_MALFORMED', str(exc)) from None
    if root.tag != _ids('ids'):
        raise IdsError('IDS_NOT_IDS', f'root element {root.tag!r} is not {{{IDS_NS}}}ids')
    title = root.findtext(f'{_ids("info")}/{_ids("title")}')
    container = root.find(_ids('specifications'))
    if container is None:
        raise IdsError('IDS_INVALID', 'missing <specifications>')
    specifications = []
    for element in _children(container):
        if element.tag != _ids('specification'):
            raise IdsError('IDS_INVALID', f'unexpected <{_local(element)}> in <specifications>')
        name = element.get('name')
        extra = sorted(set(element.attrib) - SPECIFICATION_ATTRIBUTES)
        if extra:
            raise IdsError('IDS_UNSUPPORTED_ATTRIBUTE', f'specification attributes {extra} are not supported (IDS 1.0 puts minOccurs/maxOccurs on applicability)')
        versions = (element.get('ifcVersion') or '').split()
        if not name:
            raise IdsError('IDS_INVALID', 'specification without name')
        if not versions:
            raise IdsError('IDS_INVALID', f'specification {name!r} without ifcVersion')
        bad = [v for v in versions if v not in IFC_VERSIONS]
        if bad:
            raise IdsError('IDS_INVALID', f'specification {name!r} ifcVersion {bad} not in {list(IFC_VERSIONS)}')
        applicability = element.find(_ids('applicability'))
        if applicability is None:
            raise IdsError('IDS_INVALID', f'specification {name!r} without applicability')
        cardinality = _occurs(applicability)
        applies = [_parse_facet(f, False) for f in _children(applicability)]
        if not applies:
            raise IdsError('IDS_INVALID', f'specification {name!r} has an empty applicability')
        if sum(f.kind == 'entity' for f in applies) != 1:
            raise IdsError('IDS_UNSUPPORTED_FACET', f'specification {name!r}: applicability requires exactly one entity facet')
        requirements = []
        node = element.find(_ids('requirements'))
        for child in _children(element):
            if child.tag not in (_ids('applicability'), _ids('requirements')):
                raise IdsError('IDS_INVALID', f'unexpected <{_local(child)}> in specification {name!r}')
        if node is not None:
            requirements = [_parse_facet(f, True) for f in _children(node)]
        if cardinality == 'prohibited' and requirements:
            raise IdsError('IDS_UNSUPPORTED_CARDINALITY', f'prohibited specification {name!r} with requirements is not supported')
        specifications.append(Specification(name, element.get('identifier'), versions, cardinality, applies, requirements))
    if not specifications:
        raise IdsError('IDS_INVALID', 'IDS contains no specification')
    return IdsDocument(title, specifications)


# --------------------------------------------------------------------------- IFC evaluation

def _attr(element, name):
    if not re.fullmatch(r'[A-Z][A-Za-z0-9]*', name):
        return None
    try:
        return getattr(element, name)
    except (AttributeError, RuntimeError):
        return None


def _type_object(element):
    for rel in _attr(element, 'IsTypedBy') or ():
        return rel.RelatingType
    for rel in _attr(element, 'IsDefinedBy') or ():  # IFC2X3 stores the type relation here
        if rel.is_a('IfcRelDefinesByType'):
            return rel.RelatingType
    return None


def _resolved_predefined(item, user_attribute):
    value = _attr(item, 'PredefinedType')
    if value == 'USERDEFINED':
        return _attr(item, user_attribute)
    return value


def _predefined_type(element):
    """IDS 1.0: a type object's PredefinedType (other than NOTDEFINED) takes precedence over the occurrence."""
    kind = _type_object(element)
    if kind is not None and _attr(kind, 'PredefinedType') not in (None, 'NOTDEFINED'):
        return _resolved_predefined(kind, 'ElementType')
    return _resolved_predefined(element, 'ObjectType')


_PREFIX = {'EXA': 1e18, 'PETA': 1e15, 'TERA': 1e12, 'GIGA': 1e9, 'MEGA': 1e6, 'KILO': 1e3, 'HECTO': 1e2, 'DECA': 1e1,
           'DECI': 1e-1, 'CENTI': 1e-2, 'MILLI': 1e-3, 'MICRO': 1e-6, 'NANO': 1e-9, 'PICO': 1e-12, 'FEMTO': 1e-15, 'ATTO': 1e-18}
_SCALED_MEASURES = {
    'IFCLENGTHMEASURE': ('LENGTHUNIT', 'METRE', 1), 'IFCPOSITIVELENGTHMEASURE': ('LENGTHUNIT', 'METRE', 1),
    'IFCNONNEGATIVELENGTHMEASURE': ('LENGTHUNIT', 'METRE', 1), 'IFCAREAMEASURE': ('AREAUNIT', 'SQUARE_METRE', 2),
    'IFCVOLUMEMEASURE': ('VOLUMEUNIT', 'CUBIC_METRE', 3),
}
_UNITLESS = {'IFCREAL', 'IFCINTEGER', 'IFCNUMERICMEASURE', 'IFCCOUNTMEASURE', 'IFCRATIOMEASURE', 'IFCPOSITIVERATIOMEASURE',
             'IFCNORMALISEDRATIOMEASURE', 'IFCPARAMETERVALUE', 'IFCMONETARYMEASURE', 'IFCPOSITIVEINTEGER'}


class _Units:
    """IDS 1.0 compares numeric measures in SI units; convert or fail closed."""

    def __init__(self, model):
        units = []
        for project in model.by_type('IfcProject'):
            assignment = _attr(project, 'UnitsInContext')
            units = list(_attr(assignment, 'Units') or ()) if assignment is not None else []
            break
        self.units = units
        self.si_base = all(self._is_si_base(u) for u in units)

    @staticmethod
    def _is_si_base(unit):
        if unit.is_a('IfcSIUnit'):
            prefix = _attr(unit, 'Prefix')
            return prefix is None or (prefix == 'KILO' and _attr(unit, 'Name') == 'GRAM')
        if unit.is_a('IfcDerivedUnit'):
            return all(_Units._is_si_base(e.Unit) for e in _attr(unit, 'Elements') or ())
        return unit.is_a('IfcMonetaryUnit')

    def _scale(self, unit_type, base_name, power, label):
        for unit in self.units:
            if _attr(unit, 'UnitType') != unit_type:
                continue
            if unit.is_a('IfcSIUnit') and _attr(unit, 'Name') == base_name:
                return _PREFIX.get(_attr(unit, 'Prefix'), 1.0) ** power
            raise IdsError('IFC_UNSUPPORTED_UNIT', f'{label}: {unit_type} {unit.is_a()} cannot be converted to SI')
        return 1.0

    def property_value(self, nominal, label):
        raw = getattr(nominal, 'wrappedValue', None)
        if not _is_number(raw) or not hasattr(nominal, 'is_a'):
            return nominal
        kind = nominal.is_a().upper()
        if kind in _SCALED_MEASURES:
            return raw * self._scale(*_SCALED_MEASURES[kind], label)
        if kind in _UNITLESS or not kind.endswith('MEASURE') or self.si_base:
            return raw
        raise IdsError('IFC_UNSUPPORTED_UNIT', f'{label}: {kind} compared in a model whose units are not SI base units')

    def attribute_value(self, raw, label):
        if isinstance(raw, float) and not self.si_base:
            raise IdsError('IFC_UNSUPPORTED_UNIT', f'{label}: numeric attribute compared in a model whose units are not SI base units')
        return raw


def _property_sets(element):
    """Yield property definitions, type first so occurrence values can override."""
    kind = _type_object(element)
    if kind is not None:
        yield from _attr(kind, 'HasPropertySets') or ()
    for rel in _attr(element, 'IsDefinedBy') or ():
        if rel.is_a('IfcRelDefinesByProperties'):
            definition = rel.RelatingPropertyDefinition
            if isinstance(definition, (list, tuple)):
                yield from definition
            else:
                yield definition


def _properties(element, facet):
    """Return {(pset, name): nominal value entity} for names matching the facet."""
    found = {}
    for definition in _property_sets(element):
        pset_name = _attr(definition, 'Name')
        if not facet.parts['propertySet'].matches(pset_name):
            continue
        if definition.is_a('IfcElementQuantity'):
            raise IdsError('IFC_UNSUPPORTED_PROPERTY_KIND', f'quantity set {pset_name!r} matched; quantities are not supported')
        if not definition.is_a('IfcPropertySet'):
            raise IdsError('IFC_UNSUPPORTED_PROPERTY_KIND', f'{definition.is_a()} {pset_name!r} matched; only IfcPropertySet is supported')
        for prop in definition.HasProperties or ():
            if not facet.parts['baseName'].matches(prop.Name):
                continue
            if not prop.is_a('IfcPropertySingleValue'):
                raise IdsError('IFC_UNSUPPORTED_PROPERTY_KIND', f'{prop.is_a()} {pset_name}.{prop.Name} matched; only IfcPropertySingleValue is supported')
            found[(pset_name, prop.Name)] = prop.NominalValue
    return {k: v for k, v in found.items() if _normalise(v) is not None}


def _classifications(element):
    refs = []
    sources = [element]
    kind = _type_object(element)
    if kind is not None:
        sources.append(kind)
    for source in sources:
        for rel in _attr(source, 'HasAssociations') or ():
            if not rel.is_a('IfcRelAssociatesClassification'):
                continue
            item = rel.RelatingClassification
            if item.is_a('IfcClassification'):
                refs.append((None, item.Name))
                continue
            identification = _attr(item, 'Identification') or _attr(item, 'ItemReference')
            system, seen = _attr(item, 'ReferencedSource'), 0
            while system is not None and system.is_a('IfcClassificationReference') and seen < 32:
                system, seen = _attr(system, 'ReferencedSource'), seen + 1
            refs.append((identification, _attr(system, 'Name') if system is not None else None))
    return refs


def _evaluate(facet, element, units):
    """Return (present, satisfied, description) for one facet on one element."""
    parts = facet.parts
    if facet.kind == 'entity':
        name_ok = parts['name'].matches(element.is_a().upper())
        type_ok = 'predefinedType' not in parts or parts['predefinedType'].matches(_predefined_type(element))
        text = f"entity {parts['name'].describe()}" + (f" with predefinedType {parts['predefinedType'].describe()}" if 'predefinedType' in parts else '')
        return True, name_ok and type_ok, text
    if facet.kind == 'attribute':
        raw = [_attr(element, n) for n in parts['name'].values]
        present = [v for v in raw if v is not None and v != '' and v != ()]
        text = f"attribute {parts['name'].describe()}" + (f" = {parts['value'].describe()}" if 'value' in parts else '')
        if not present:
            return False, False, text
        ok = all('value' not in parts or parts['value'].matches(units.attribute_value(v, text)) for v in present)
        return True, ok, text
    if facet.kind == 'property':
        found = _properties(element, facet)
        text = f"property {parts['propertySet'].describe()}.{parts['baseName'].describe()}"
        if 'value' in parts:
            text += f" = {parts['value'].describe()}"
        if facet.data_type:
            text += f' ({facet.data_type})'
        if not found:
            return False, False, text
        ok = True
        for nominal in found.values():
            if facet.data_type and (not hasattr(nominal, 'is_a') or nominal.is_a().upper() != facet.data_type):
                ok = False
            if 'value' in parts and not parts['value'].matches(units.property_value(nominal, text)):
                ok = False
        return True, ok, text
    refs = _classifications(element)
    if 'system' in parts:
        refs = [r for r in refs if parts['system'].matches(r[1])]
    text = 'classification' + (f" system {parts['system'].describe()}" if 'system' in parts else '') + (f" value {parts['value'].describe()}" if 'value' in parts else '')
    if not refs:
        return False, False, text
    ok = 'value' not in parts or any(parts['value'].matches(r[0]) for r in refs)
    return True, ok, text


def _requirement_failure(facet, element, units):
    present, satisfied, text = _evaluate(facet, element, units)
    if facet.cardinality == 'required' and not satisfied:
        return f'required {text} ' + ('not satisfied' if present else 'missing')
    if facet.cardinality == 'prohibited' and satisfied:
        return f'prohibited {text} present'
    if facet.cardinality == 'optional' and present and not satisfied:
        return f'optional {text} present but not satisfied'
    return None


def _candidates(model, facets):
    entity = next(f for f in facets if f.kind == 'entity')
    found = {}
    for name in entity.parts['name'].values:
        try:
            items = model.by_type(name, include_subtypes=False)
        except Exception:  # ifcopenshell signals unknown schema entities with varying types
            raise IdsError('IDS_UNKNOWN_ENTITY', f'{name} is not an entity of schema {model.schema}') from None
        for item in items:
            found[item.id()] = item
    return [found[k] for k in sorted(found)]


def _failure(element, reason):
    return {'entity': element.is_a(), 'global_id': _attr(element, 'GlobalId'), 'step_id': element.id(), 'reason': reason}


def evaluate(document, model):
    """Evaluate a parsed IdsDocument against an open ifcopenshell model."""
    schema = normalise_schema(model.schema)
    units = _Units(model)
    results = []
    for spec in document.specifications:
        entry = {'name': spec.name, 'identifier': spec.identifier, 'cardinality': spec.cardinality,
                 'ifc_versions': spec.ifc_versions, 'applicable_count': 0, 'status': 'pass', 'reason': None, 'failures': []}
        results.append(entry)
        if not any(v.upper() == schema for v in spec.ifc_versions):
            entry.update(status='skipped', reason=f'ifcVersion {spec.ifc_versions} does not include {schema}')
            continue
        applicable = [e for e in _candidates(model, spec.applicability)
                      if all(_evaluate(f, e, units)[1] for f in spec.applicability)]
        entry['applicable_count'] = len(applicable)
        failures = []
        if spec.cardinality == 'prohibited':
            failures = [_failure(e, 'prohibited specification: entity matches applicability') for e in applicable]
        else:
            for element in applicable:
                for facet in spec.requirements:
                    reason = _requirement_failure(facet, element, units)
                    if reason:
                        failures.append(_failure(element, reason))
            if spec.cardinality == 'required' and not applicable:
                entry['reason'] = 'required specification has no applicable entities'
        failures.sort(key=lambda f: (f['global_id'] or '', f['step_id'], f['reason']))
        entry['failures'] = failures
        if failures or entry['reason']:
            entry['status'] = 'fail'
            entry['reason'] = entry['reason'] or f'{len(failures)} failing requirement(s)'
    counts = {'specifications': len(results), 'passed': sum(r['status'] == 'pass' for r in results),
              'failed': sum(r['status'] == 'fail' for r in results), 'skipped': sum(r['status'] == 'skipped' for r in results)}
    status, reason = 'pass', None
    if counts['failed']:
        status, reason = 'fail', f"{counts['failed']} specification(s) failed"
    elif counts['passed'] == 0:
        status, reason = 'fail', f'no specification applies to IFC schema {schema}'
    return status, reason, counts, results


def _digest(data):
    return hashlib.sha256(data).hexdigest()


def check(ids_path, ifc_path):
    """Check an IFC file against an IDS file; always return a report dict."""
    ids_path, ifc_path = Path(ids_path), Path(ifc_path)
    report = {'schema': REPORT_SCHEMA, 'ids': {'file': ids_path.name, 'sha256': None, 'title': None},
              'ifc': {'file': ifc_path.name, 'sha256': None}, 'ifc_schema': None, 'status': 'invalid', 'reason': None,
              'error': None, 'counts': {'specifications': 0, 'passed': 0, 'failed': 0, 'skipped': 0}, 'specifications': []}
    try:
        try:
            ids_bytes = ids_path.read_bytes()
        except OSError as exc:
            raise IdsError('IDS_UNREADABLE', exc.strerror or type(exc).__name__) from None
        report['ids']['sha256'] = _digest(ids_bytes)
        document = parse_ids(ids_bytes)
        report['ids']['title'] = document.title
        try:
            report['ifc']['sha256'] = _digest(ifc_path.read_bytes())
        except OSError as exc:
            raise IdsError('IFC_UNREADABLE', exc.strerror or type(exc).__name__) from None
        import ifcopenshell  # deferred so IDS parsing works without the IFC venv
        try:
            model = ifcopenshell.open(str(ifc_path))
        except Exception as exc:  # ifcopenshell raises several unrelated types
            raise IdsError('IFC_UNREADABLE', f'{type(exc).__name__}') from None
        report['ifc_schema'] = normalise_schema(model.schema)
        status, reason, counts, results = evaluate(document, model)
        report.update(status=status, reason=reason, counts=counts, specifications=results)
    except IdsError as exc:
        _invalidate(report, exc.code, exc.message)
    except Exception as exc:  # fail closed: never a traceback or a silent pass
        _invalidate(report, 'INTERNAL', f'unexpected {type(exc).__name__}')
    return report


def _invalidate(report, code, message):
    report.update(status='invalid', reason=message, error={'code': code, 'message': message},
                  counts={'specifications': 0, 'passed': 0, 'failed': 0, 'skipped': 0}, specifications=[])


EXIT_CODES = {'pass': 0, 'fail': 1, 'invalid': 2}


def render(report):
    return json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description='Check an IFC file against a supported IDS 1.0 subset.')
    parser.add_argument('ids')
    parser.add_argument('ifc')
    parser.add_argument('--output', help='also write the JSON report to this path')
    args = parser.parse_args(argv)
    try:
        report = check(args.ids, args.ifc)
        text = render(report)
    except Exception as exc:  # check() already fails closed; this guards rendering
        report = {'schema': REPORT_SCHEMA, 'specifications': []}
        _invalidate(report, 'INTERNAL', f'unexpected {type(exc).__name__}')
        text = render(report)
    if args.output:
        try:
            Path(args.output).write_text(text, encoding='utf-8')
        except OSError as exc:
            _invalidate(report, 'OUTPUT_UNWRITABLE', f'cannot write report: {exc.strerror or type(exc).__name__}')
            text = render(report)
    sys.stdout.write(text)
    return EXIT_CODES[report['status']]


if __name__ == '__main__':
    sys.exit(main())
