import type { GuaNode } from "./core";

export interface CodeChoice { id: string; label: string; code: string }
export interface GeneratedNodeCode {
  locators: CodeChoice[];
  actions: CodeChoice[];
  states: CodeChoice[];
}

/** Regular C# literal; escape UTF-16 code units to preserve even lone surrogates. */
export function csharpString(value: string): string {
  let result = '"';
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit === 34) result += '\\"';
    else if (unit === 92) result += "\\\\";
    else if (unit < 32 || unit > 126) result += `\\u${unit.toString(16).padStart(4, "0")}`;
    else result += value[i];
  }
  return result + '"';
}

/** Produces source only. `context` is the caller's IGuaContext; never evaluated here. */
export function generateNodeCode(node: GuaNode, locatorId = "id", sensitive = false): GeneratedNodeCode {
  const root = "GuaAssertions.Query(context)";
  const role = `${root}.ByRole(${csharpString(node.role)}${node.label === undefined ? "" : `, ${csharpString(node.label)}`})`;
  const locators: CodeChoice[] = [
    { id: "id", label: "Exact ID (verify its lifetime in your game)", code: `${root}.ById(${csharpString(node.id)})` },
  ];
  // The native selector treats an empty name as no criterion, not an exact label.
  if (node.label !== "") {
    locators.push({ id: "role", label: "Role / name (must match exactly one node)", code: role });
    if (node.parentId !== undefined) locators.push({ id: "scope", label: "Role / name within parent", code: `${role}.Within(${csharpString(node.parentId)}, directChild: true)` });
  }
  const locator = locators.find((choice) => choice.id === locatorId)?.code ?? locators[0]!.code;
  const prefix = `// using Gua.Testing; context is an IGuaContext.\nvar locator = ${locator};\n`;
  const calls: Record<string, string> = {
    click: "ClickAsync()",
    focus: "FocusAsync()",
    set_value: sensitive ? 'SetValueAsync(secretValue, sensitive: true)' : `SetValueAsync(${csharpString("")})`,
    set_checked: "SetCheckedAsync(true)",
    select: `SelectAsync(${csharpString("")})`,
    scroll: "ScrollAsync(0f, 1f, unit: 1)",
    press_key: `PressKeyAsync(${csharpString("Enter")})`,
  };
  const actions = Object.keys(calls).filter((action) => node.actions.includes(action)).map((action) => ({
    id: action, label: action,
    code: prefix + (action === "set_value" && sensitive ? "// Supply secretValue from your test's secret provider.\n" : "") +
      (["set_value", "select", "set_checked", "scroll", "press_key"].includes(action) ? "// Edit the action argument for your test.\n" : "") +
      `await locator.${calls[action]};`,
  }));
  const states: CodeChoice[] = [];
  const add = (id: string, wait: string) => states.push({
    // The wait itself verifies the condition. A trailing assertion rereads the
    // host and can reject a transient state that the wait already observed.
    id, label: id, code: prefix + `var node = await locator.ResolveAsync();\nawait node.${wait};`,
  });
  if (node.visible) add("visible", "WaitUntilVisibleAsync()");
  else states.push({ id: "visible", label: "visible",
    code: prefix + '// Hidden or removed; reject ambiguous matches before waiting on the ID.\nvar matches = locator.QueryAll();\nif (matches.Count > 1)\n    throw new GuaAssertionException("Hidden locator must match at most one node.");\nif (matches.Count == 1)\n    await GuaAssertions.WaitForHiddenAsync(context, matches[0].Id);',
  });
  add("enabled", node.enabled ? "WaitUntilEnabledAsync()" : "WaitUntilDisabledAsync()");
  if (!sensitive && typeof node.text === "string") add("text", `WaitForTextAsync(${csharpString(node.text)})`);
  // Gua.Testing reads top-level value, not legacy state.value. Numeric JSON spelling
  // can differ (1, 1.0, 1e0), so compare numeric meaning using invariant culture.
  const value = node.value;
  if (!sensitive && value === null) states.push({
    id: "value", label: "value",
    code: prefix + 'var node = await locator.ResolveAsync();\nawait GuaAssertions.WaitForStateAsync(context, node.Id,\n    snapshot => snapshot.HasValue && snapshot.Value is null,\n    description: "have an observed null value");',
  });
  if (!sensitive && (typeof value === "string" || typeof value === "boolean")) add("value", `WaitForValueAsync(${csharpString(String(value))})`);
  // JSON parsing has already lost the raw token for unsafe integers. Do not
  // assert a rounded value that could also match a different adjacent integer.
  if (!sensitive && typeof value === "number" && Number.isFinite(value) &&
    (!Number.isInteger(value) || Number.isSafeInteger(value))) states.push({
    id: "value", label: "value",
    code: prefix + `var node = await locator.ResolveAsync();\nawait GuaAssertions.WaitForStateAsync(context, node.Id, snapshot =>\n    double.TryParse(snapshot.Value, System.Globalization.NumberStyles.Float,\n        System.Globalization.CultureInfo.InvariantCulture, out var value) && value == ${String(value)}d,\n    description: "have the observed numeric value");`,
  });
  for (const state of ["checked", "selected", "focused"] as const) {
    const observed = node.state?.[state];
    if (typeof observed !== "boolean") continue;
    const name = state[0]!.toUpperCase() + state.slice(1);
    add(state, `WaitUntil${name}Async(${observed})`);
  }
  return { locators: locators.map((choice) => ({ ...choice, code: `// using Gua.Testing; context is an IGuaContext.\nvar locator = ${choice.code};` })), actions, states };
}

export async function copyGeneratedCode(code: string, write: (code: string) => Promise<void>): Promise<string> {
  try {
    await write(code);
    return "Copied C# code.";
  } catch {
    return "Clipboard unavailable. Select the code field and copy manually.";
  }
}
