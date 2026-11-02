"""Skill manifest schema agreement check (P5-01).

Validates the checked-in sample skills and the corpus in tests/skills/corpus/cases.json against
docs/contracts/skill.schema.json with jsonschema (Draft 2020-12):
  * every sample skill and every valid corpus case must validate;
  * every invalid case tagged schema=true must be rejected by the schema;
  * every invalid case tagged schema=false that is plain JSON (no duplicate members) must be accepted, so the tag
    stays honest about what the schema cannot express.
The C# oracle (tests/skills/Program.cs) checks the same corpus against SkillParser. Run with: python3 -I tests/skills/check_schema.py
"""
import json
import pathlib
import sys

from jsonschema import Draft202012Validator

ROOT = pathlib.Path(__file__).resolve().parents[2]
passed = 0
failed = 0


def check(condition, name, detail=""):
    global passed, failed
    if condition:
        print("PASS " + name)
        passed += 1
    else:
        print("::error::skill-schema FAIL " + name + (" | " + detail if detail else ""))
        failed += 1


def strict_pairs(pairs):
    keys = [key for key, _ in pairs]
    if len(keys) != len(set(keys)):
        raise ValueError("duplicate member")
    return dict(pairs)


def load_plain(text):
    """The document as Python data, or None when it is not plain JSON (syntax error, BOM, duplicate members)."""
    try:
        return json.loads(text, object_pairs_hook=strict_pairs)
    except ValueError:
        return None


def errors(validator, document):
    return sorted(error.message for error in validator.iter_errors(document))


def main():
    schema = json.loads((ROOT / "docs" / "contracts" / "skill.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    check(True, "schema is a valid Draft 2020-12 schema")

    samples = sorted((ROOT / "skills").glob("*/skill.json"))
    check(len(samples) >= 2, "sample skills are present", str(len(samples)))
    for path in samples:
        document = json.loads(path.read_text(encoding="utf-8"))
        found = errors(validator, document)
        check(not found, "sample " + path.parent.name + " validates", "; ".join(found))
        check(document.get("id") == path.parent.name, "sample " + path.parent.name + " id equals its directory")

    corpus = json.loads((ROOT / "tests" / "skills" / "corpus" / "cases.json").read_text(encoding="utf-8"))
    check(len(corpus["valid"]) > 0 and len(corpus["invalid"]) > 0, "corpus has valid and invalid cases")
    for case in corpus["valid"]:
        found = errors(validator, case["manifest"])
        check(not found, "valid case validates: " + case["name"], "; ".join(found))
    for case in corpus["invalid"]:
        name = case["name"]
        if case["schema"]:
            check("manifest" in case, "schema-expressible case is a JSON object: " + name)
            if "manifest" in case:
                check(not validator.is_valid(case["manifest"]), "schema rejects: " + name)
            continue
        document = case["manifest"] if "manifest" in case else load_plain(case["text"])
        if document is None:
            print("NOTE not plain JSON, parser-only: " + name)
            continue
        found = errors(validator, document)
        check(not found, "schema accepts parser-only refusal (tag is honest): " + name, "; ".join(found))

    print(json.dumps({"passed": passed, "failed": failed, "skill_schema": failed == 0}))
    return 0 if failed == 0 and passed > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
