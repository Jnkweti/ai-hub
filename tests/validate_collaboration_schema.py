"""Independent JSON Schema parity check; jsonschema is a test-only dependency."""
import json
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "artifacts" / "schema-validation"))
from jsonschema import Draft202012Validator

schema = json.loads((root / "docs/collaboration/submission.schema.json").read_text())
fixtures = json.loads((root / "docs/collaboration/contract-fixtures.json").read_text())
Draft202012Validator.check_schema(schema)
validator = Draft202012Validator(schema)
for fixture in fixtures:
    errors = list(validator.iter_errors(fixture["input"]))
    assert (not errors) == fixture["valid"], (fixture["name"], [e.message for e in errors])
print(f"PASS published JSON Schema matches all {len(fixtures)} contract fixtures")
