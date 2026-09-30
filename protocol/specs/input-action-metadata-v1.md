# Input Action metadata v1 (#121 / OPEN-01)

This additive contract preserves the existing `description`, `valueType`,
`range`, holdability, activity, risk, exposure and confirmation semantics.
Metadata describes player actions and input axes; never publish secrets in
descriptions, schemas or examples. Examples are public design-time literals,
not captured input, confirmation evidence or permission to execute/replay.

## Compatibility and explicit selection

The existing map/search schemas, commands and C ABI descriptor v1/v2 remain
unchanged. Their responses remain `schemaVersion: 1` and omit both new fields,
even if the host registered metadata. Do not add fields to strict old validators.
Legacy hosts and registrations continue to work without repeating descriptions.

Hosts advertise `semantic_game_input_metadata_v1` only where semantic game input
is supported. Capable consumers explicitly invoke `get_game_input_actions_v2`
or `find_game_input_actions_v2`; these return `schemaVersion: 2` using the
separate v2 map/search schemas. Search filters/order/limits and Player projection
are unchanged. A new client falls back to v1 when metadata capability is absent;
it must not pretend metadata was validated or provided by an old host.
No new fields are added to legacy commands. Metadata commands have a separate
`game-input-metadata-commands-v1.schema.json` contract.

C adds descriptor v3 with a complete embedded descriptor v2 and optional
NUL-terminated `value_schema_json` / `examples_json`, plus separate opt-in copy
and query functions. Null pointers mean absent; empty JSON strings and JSON null
are invalid. Existing struct sizes, layouts and symbols stay intact. C++ and
.NET retain old methods/record constructors; new methods select wire v2. .NET
registration uses init properties rather than changing positional constructors.
Godot registration accepts `value_schema` (Dictionary) and `examples` (Array);
Unity/.NET/C++ accept optional JSON strings. Metadata-free registration continues
through descriptor v2. New clients on old native libraries must use the old
paths unless the capability is present; calling new ABI symbols requires the
matching new native library. No release/package version is advanced here.

## Supported dialect and declaration consistency

`valueSchema` is a bounded JSON Schema draft 2020-12 subset. It is optional, and
`examples` is an independent optional array of at most 16 **Set value** literals.
Each JSON document is at most 16384 UTF-8 bytes. A schema is an object with a
required single `type`, optional `description` (at most 1024 Unicode code points),
and optional root `$schema` equal to
`https://json-schema.org/draft/2020-12/schema`. Supported shapes:

| valueType | schema type | Extra keywords |
|---|---|---|
| button | boolean | none |
| axis1d | number | finite `minimum`, `maximum` |
| vector2 | object | required `properties` containing exactly x/y number schemas, `required` containing x/y exactly once, `additionalProperties: false` |
| text | string | integer `minLength`, `maxLength`, each 0–40 |

Vector children permit `type`, `description`, `minimum`, `maximum` only.
Unknown keywords (including `$ref`, regex, composition, enum, defaults, arrays,
nullable values, nested objects and external resources) are rejected, never
silently ignored or fetched. Invalid Unicode and duplicate object keys are
rejected. Numeric values must be finite. Text length counts Unicode code points,
not UTF-16 units; the existing 40-code-point transport limit always applies.

The existing `valueType` determines the shape; schemas cannot redefine it.
Existing range applies to axis1d and both vector coordinates. Schema bounds may
narrow it but may not widen it; omitted bounds inherit the existing range for
declaration checking and input validation. Without range, omitted bounds impose
no extra constraint. Reversed bounds/lengths or range on a metadata-bearing
button/text declaration are rejected. Schema and range are a single combined
contract: a standalone JSON Schema evaluator also needs the descriptor range.

Examples must satisfy the valueType, range, schema and transport limit together.
Each original example JSON literal must be shorter than the existing 512-byte
Set payload buffer, including whitespace within the literal. Surrounding array
formatting does not count. Discovery never publishes an oversized Set example.
For non-holdable buttons, examples must be absent or empty: Set is unsupported.
Press and Release do not take values and have no value examples. No internal
game-command parameters or new value types are introduced.

## Registration, execution and public revision

Native registration validates declarations and every example before committing
the frame. Failure invalidates the staging frame; the previous map survives.
Get and search return the same original schema/examples, not synthesized data.
Public metadata changes advance the projected action revision; private-only
changes do not advance Player revision. Player filtering happens before search
count/limit and before metadata serialization.

Metadata-bearing actions validate Set payload shape and constraints both on
enqueue and host consumption against the **current** map, even for old callers.
Metadata-free actions retain their existing validation. Capability, active
context, ownership and current confirmation rules still apply. Release and
cleanup remain available without satisfying Set constraints. Examples are never
automatically executed, stored as recorded user input or converted to confirmed.
Recording/Replay must retain their current fresh-confirmation and secret handling.

## Example

For a `vector2` action with existing range [-1, 1]:

```json
{
  "valueSchema": {
    "type": "object",
    "properties": {
      "x": {"type": "number", "description": "Horizontal movement; positive moves right"},
      "y": {"type": "number", "description": "Forward movement; positive moves forward"}
    },
    "required": ["x", "y"],
    "additionalProperties": false
  },
  "examples": [{"x": 0.5, "y": 0}]
}
```

`{"x":2,"y":0}`, an extra `z`, a nested `x`, a non-finite number,
or a mismatching schema type is rejected. Schema/examples do not authorize
movement or bypass a newly required confirmation.
