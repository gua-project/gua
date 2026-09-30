import { describe, expect, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { csharpString, copyGeneratedCode, generateNodeCode } from "../src/codegen";
import { NodeCodePanel } from "../src/NodeCodePanel";
import type { GuaNode } from "../src/core";

const hostile = '"\\\r\n\t\0\u0001\u007f\u0085\u2028日本語😀\ud800';
const node: GuaNode = {
  id: hostile, parentId: hostile, role: "textbox", label: hostile,
  text: hostile, value: hostile, visible: true, enabled: false, bounds: {},
  actions: ["click", "focus", "set_value", "set_checked", "select", "scroll", "press_key"],
  state: { checked: false, selected: false, focused: false },
};

describe("Inspector C# generation", () => {
  test("preserves input and uses delayed exact locators and auto-wait actions", () => {
    const before = JSON.stringify(node);
    const output = generateNodeCode(node, "scope");
    expect(JSON.stringify(node)).toBe(before);
    expect(output.locators.map((choice) => choice.id)).toEqual(["id", "role", "scope"]);
    expect(output.locators[0]!.code).toContain(`.ById(${csharpString(hostile)})`);
    expect(output.actions).toHaveLength(7);
    expect(output.actions[0]!.code).toContain(".Within(");
    expect(output.actions[0]!.code).toContain("await locator.ClickAsync()");
    expect(output.actions[0]!.code).not.toContain(".Get()");
    expect(output.states.map((choice) => choice.id)).toEqual(["visible", "enabled", "text", "value", "checked", "selected", "focused"]);
    expect(output.states.find((choice) => choice.id === "checked")!.code).toContain("WaitUntilCheckedAsync(false)");
  });
  test("does not invent optional state, label, scope or actions", () => {
    const minimal: GuaNode = { id: "id", role: "button", visible: false, enabled: true, bounds: {}, actions: ["unknown"] };
    const output = generateNodeCode(minimal);
    expect(output.actions).toEqual([]);
    expect(output.states.map((choice) => choice.id)).toEqual(["visible", "enabled"]);
    expect(output.locators).toHaveLength(2);
    expect(output.locators[1]!.code).toContain('.ByRole("button")');
    expect(output.states[0]!.code).toContain("matches.Count > 1");
    expect(output.states[0]!.code).toContain("WaitForHiddenAsync(context, matches[0].Id)");
    expect(output.states[0]!.code).not.toContain("ResolveAsync()");
    expect(generateNodeCode({ ...minimal, value: "", text: "", state: { value: false } }).states).toHaveLength(4);
  });
  test("empty labels omit native name locators instead of broadening them", () => {
    const output = generateNodeCode({ ...node, label: "" }, "scope");
    expect(output.locators.map((choice) => choice.id)).toEqual(["id"]);
    expect(output.actions[0]!.code).toContain(".ById(");
    expect(output.actions[0]!.code).not.toContain(".ByRole(");
  });
  test("sensitive mode omits text/value and uses a secret variable", () => {
    const output = generateNodeCode(node, "id", true);
    expect(output.states.map((choice) => choice.id)).not.toContain("value");
    expect(output.states.map((choice) => choice.id)).not.toContain("text");
    expect(output.actions.find((choice) => choice.id === "set_value")!.code).toContain("secretValue, sensitive: true");
  });
  test("explicit null is observable and differs from omitted value", () => {
    const output = generateNodeCode({ ...node, value: null });
    expect(output.states.find((choice) => choice.id === "value")!.code).toContain("snapshot.HasValue && snapshot.Value is null");
    const omitted = { ...node }; delete omitted.value;
    expect(generateNodeCode(omitted).states.map((choice) => choice.id)).not.toContain("value");
    expect(generateNodeCode({ ...node, value: null }, "id", true).states.map((choice) => choice.id)).not.toContain("value");
  });
  test("clipboard success and rejection retain the source for manual copying", async () => {
    let written = "";
    const code = generateNodeCode(node).locators[0]!.code;
    expect(await copyGeneratedCode(code, async (source) => { written = source; })).toBe("Copied C# code.");
    expect(written).toBe(code);
    expect(await copyGeneratedCode(code, async () => { throw new Error("denied"); })).toContain("copy manually");
    expect(await copyGeneratedCode(code, () => { throw new Error("API missing"); })).toContain("copy manually");
  });
  test("node detail renders copy controls and a selectable code field without running actions", () => {
    const html = renderToStaticMarkup(<NodeCodePanel node={node} sensitive={false} />);
    for (const text of ["Copy locator", "Copy action", "Copy wait/assertion", "Generated C# code", "readOnly"]) expect(html).toContain(text);
    expect(renderToStaticMarkup(<NodeCodePanel node={{ ...node, actions: [] }} sensitive={false} />)).not.toContain("Copy action");
  });
  test("all generated API calls compile against real Gua.Testing and literals round-trip", async () => {
    const directory = mkdtempSync(join(tmpdir(), "gua-inspector-codegen-"));
    const project = resolve(import.meta.dir, "../../..", "bindings/dotnet/src/Gua.Testing/Gua.Testing.csproj");
    const snippets: string[] = [];
    for (const sample of [node, { ...node, value: null }, { ...node, value: 1.5e-20 }, { ...node, value: false }, { ...node, visible: false, enabled: true, state: { checked: true, selected: true, focused: true } }]) {
      for (const locatorId of ["id", "role", "scope"]) {
        for (const sensitive of [false, true]) {
          const generated = generateNodeCode(sample, locatorId, sensitive);
          snippets.push(...[...generated.locators, ...generated.actions, ...generated.states].map((choice) => choice.code));
        }
      }
    }
    const literals = [hostile, "", "\\u0041", '"; throw new Exception(); //', ...Array.from({ length: 256 }, (_, i) => String.fromCharCode(i))];
    const methods = snippets.map((code, i) => `static async Task Case${i}(IGuaContext context, string secretValue) { ${code}\n }`).join("\n");
    const runtimeMethods: string[] = [];
    const runtimeCalls: string[] = [];
    for (const value of ["expected", false, true, 1.5e-20, 1, 0, null]) {
      const sample = { ...node, id: "n", parentId: undefined, label: "", text: "expected", value };
      const tree = JSON.stringify({ schemaVersion: 2, sessionEpoch: 1, frameSequence: 1, revision: 1, screen: "test", nodes: [sample] });
      const before = JSON.stringify({ schemaVersion: 2, sessionEpoch: 1, frameSequence: 0, revision: 0, screen: "test", nodes: [{ ...sample, text: "before", value: "before" }] });
      for (const state of generateNodeCode(sample).states) {
        const method = `Runtime${runtimeMethods.length}`;
        // The host publishes the expected state for the wait, then immediately
        // reverts it. Successful waits must not reread and reject that frame.
        const reverted = JSON.stringify({ schemaVersion: 2, frameSequence: 2, revision: 2, screen: "test", nodes: [{ ...sample, visible: false, enabled: true, text: "after", value: "after", state: { checked: true, selected: true, focused: true } }] });
        const code = state.code.replace("var node = await locator.ResolveAsync();", `var node = await locator.ResolveAsync();\n((Fixture)context).Json = ${csharpString(tree)};\n((Fixture)context).NextJson = ${csharpString(reverted)};\n((Fixture)context).ReadsUntilNextJson = 2;`);
        runtimeMethods.push(`static async Task ${method}(IGuaContext context) { ${code}\n }`);
        runtimeCalls.push(`await ${method}(new Fixture(${csharpString(before)}));`);
      }
    }
    const hidden = { ...node, id: "n", parentId: "p", label: "target", text: "known text", value: "known value", visible: false };
    const hiddenTree = JSON.stringify({ schemaVersion: 2, frameSequence: 1, revision: 1, screen: "test", nodes: [hidden] });
    const visibleTree = JSON.stringify({ schemaVersion: 2, frameSequence: 1, revision: 1, screen: "test", nodes: [{ ...hidden, visible: true }] });
    const removedTree = JSON.stringify({ schemaVersion: 2, frameSequence: 2, revision: 2, screen: "test", nodes: [] });
    const hiddenCode = generateNodeCode(hidden).states.find((state) => state.id === "visible")!.code;
    runtimeMethods.push(`static async Task Hidden(IGuaContext context) { ${hiddenCode}\n }`);
    runtimeCalls.push(`await Hidden(new Fixture(${csharpString(hiddenTree)}));`, `await Hidden(new Fixture(${csharpString(removedTree)}));`,
      `await Hidden(new Fixture(${csharpString(visibleTree)}) { NextJson = ${csharpString(removedTree)} });`);
    for (const locatorId of ["role", "scope"]) {
      const code = generateNodeCode(hidden, locatorId).states.find((state) => state.id === "visible")!.code;
      const method = `Hidden${locatorId}`;
      runtimeMethods.push(`static async Task ${method}(IGuaContext context) { ${code}\n }`);
      runtimeCalls.push(`await ${method}(new Fixture(${csharpString(hiddenTree)}));`, `await ${method}(new Fixture(${csharpString(removedTree)}));`);
      for (const visible of [false, true]) {
        const duplicates = JSON.stringify({ schemaVersion: 2, screen: "test", nodes: [hidden, { ...hidden, id: "n2", visible }] });
        runtimeCalls.push(`try { await ${method}(new Fixture(${csharpString(duplicates)})); throw new Exception("Ambiguous hidden locator passed"); } catch (GuaAssertionException) { }`);
      }
    }
    const unknown = { ...hidden, text: "known text" }; delete unknown.value;
    const unknownTree = JSON.stringify({ schemaVersion: 2, frameSequence: 1, revision: 1, screen: "test", nodes: [unknown] });
    runtimeMethods.push(`static async Task UnknownValue() {
      var context = new Fixture(${csharpString(unknownTree)});
      var node = await GuaAssertions.Query(context).ById("n").ResolveAsync();
      if (node.Snapshot.HasValue) throw new Exception("Missing value incorrectly observed");
      try { await GuaAssertions.WaitForStateAsync(context, "n", snapshot => snapshot.HasValue && snapshot.Value is null, timeout: TimeSpan.Zero); }
      catch (GuaAssertionException) { return; }
      throw new Exception("Unknown value must not satisfy observed null");
    }`);
    runtimeCalls.push("await UnknownValue();");
    try {
      writeFileSync(join(directory, "Generated.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><NoWarn>CS1998</NoWarn></PropertyGroup><ItemGroup><ProjectReference Include="${project.replaceAll("&", "&amp;")}" /></ItemGroup></Project>`);
      // Compare code units directly; Encoding replaces isolated surrogates.
      const directChecks = literals.map((value) => `if (!System.Linq.Enumerable.SequenceEqual(${csharpString(value)}, new char[] { ${Array.from({ length: value.length }, (_, i) => `(char)${value.charCodeAt(i)}`).join(",")} })) throw new Exception("Literal mismatch");`).join("\n");
      const fixture = `sealed class Fixture(string json) : IGuaContext {
        public string Json = json;
        public string? NextJson;
        public int ReadsUntilNextJson;
        public string GetUiTreeJson() {
          var result = Json;
          if (ReadsUntilNextJson > 0 && --ReadsUntilNextJson == 0 && NextJson != null) { Json = NextJson; NextJson = null; }
          return result;
        }
        public GuaNodeState GetNodeState(string id) => new(true, false);
        public string FindNodeById(string id) => id;
        public string FindNodeByRole(string role, string? name = null) => "n";
        public string FindNodeByText(string text) => "n";
        public GuaQueryResult Query(GuaSelector selector) {
          using var document = System.Text.Json.JsonDocument.Parse(Json);
          var matches = new System.Collections.Generic.List<GuaNodeQueryMatch>();
          foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray()) {
            bool Matches(string? expected, string field) => string.IsNullOrEmpty(expected) || (node.TryGetProperty(field, out var actual) && actual.GetString() == expected);
            if (!Matches(selector.Id, "id") || !Matches(selector.Role, "role") || !Matches(selector.Name, "label") || !Matches(selector.ParentId, "parentId")) continue;
            if (selector.Visible == GuaStateFilter.True && !node.GetProperty("visible").GetBoolean()) continue;
            matches.Add(new GuaNodeQueryMatch(node.GetProperty("id").GetString()!, node.GetProperty("role").GetString()!, node.GetProperty("label").GetString(), node.TryGetProperty("parentId", out var parent) ? parent.GetString() : null));
          }
          if (ReadsUntilNextJson == 0 && NextJson != null) { Json = NextJson; NextJson = null; }
          return new(true, matches);
        }
        public bool EnqueueClick(string id) => throw new Exception("Code generation must not enqueue actions");
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId) { requestId = 0; throw new Exception("Code generation must not enqueue actions"); }
        public bool TryPollActionEvent(out GuaActionEvent e) { e = default; return false; }
        public bool TryPollActionEvent(ulong id, out GuaActionEvent e) { e = default; return false; }
        public bool TryPollEvent(out GuaEvent e) { e = default; return false; }
      }`;
      writeFileSync(join(directory, "Program.cs"), `using Gua.Core; using Gua.Testing; class Program { ${methods}\n${runtimeMethods.join("\n")}\nstatic async Task Main() { ${directChecks}\n${runtimeCalls.join("\n")} } } ${fixture}`);
      const process = Bun.spawn(["dotnet", "run", "--project", join(directory, "Generated.csproj"), "--configuration", "Release"], { stdout: "pipe", stderr: "pipe" });
      const [stdout, stderr, exitCode] = await Promise.all([new Response(process.stdout).text(), new Response(process.stderr).text(), process.exited]);
      expect({ exitCode, diagnostics: exitCode === 0 ? "" : stdout + stderr }).toEqual({ exitCode: 0, diagnostics: "" });
    } finally { rmSync(directory, { recursive: true, force: true }); }
  }, 120_000);
});
