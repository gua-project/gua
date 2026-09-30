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
    expect(output.states.find((choice) => choice.id === "checked")!.code).toContain("ToBeChecked(false)");
  });
  test("does not invent optional state, label, scope or actions", () => {
    const minimal: GuaNode = { id: "id", role: "button", visible: false, enabled: true, bounds: {}, actions: ["unknown"] };
    const output = generateNodeCode(minimal);
    expect(output.actions).toEqual([]);
    expect(output.states.map((choice) => choice.id)).toEqual(["visible", "enabled"]);
    expect(output.locators).toHaveLength(2);
    expect(output.locators[1]!.code).toContain('.ByRole("button")');
    expect(output.states[0]!.code).toContain("WaitUntilHiddenAsync()");
    expect(generateNodeCode({ ...minimal, value: "", text: "", state: { value: false } }).states).toHaveLength(4);
  });
  test("sensitive mode omits text/value and uses a secret variable", () => {
    const output = generateNodeCode(node, "id", true);
    expect(output.states.map((choice) => choice.id)).not.toContain("value");
    expect(output.states.map((choice) => choice.id)).not.toContain("text");
    expect(output.actions.find((choice) => choice.id === "set_value")!.code).toContain("secretValue, sensitive: true");
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
    for (const sample of [node, { ...node, value: 1.5e-20 }, { ...node, value: false }, { ...node, visible: false, enabled: true, state: { checked: true, selected: true, focused: true } }]) {
      for (const locatorId of ["id", "role", "scope"]) {
        for (const sensitive of [false, true]) {
          const generated = generateNodeCode(sample, locatorId, sensitive);
          snippets.push(...[...generated.locators, ...generated.actions, ...generated.states].map((choice) => choice.code));
        }
      }
    }
    const literals = [hostile, "", "\\u0041", '"; throw new Exception(); //', ...Array.from({ length: 256 }, (_, i) => String.fromCharCode(i))];
    const methods = snippets.map((code, i) => `static async Task Case${i}(IGuaContext context, string secretValue) { ${code}\n }`).join("\n");
    try {
      writeFileSync(join(directory, "Generated.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><NoWarn>CS1998</NoWarn></PropertyGroup><ItemGroup><ProjectReference Include="${project.replaceAll("&", "&amp;")}" /></ItemGroup></Project>`);
      // Compare code units directly; Encoding replaces isolated surrogates.
      const directChecks = literals.map((value) => `if (!System.Linq.Enumerable.SequenceEqual(${csharpString(value)}, new char[] { ${Array.from({ length: value.length }, (_, i) => `(char)${value.charCodeAt(i)}`).join(",")} })) throw new Exception("Literal mismatch");`).join("\n");
      writeFileSync(join(directory, "Program.cs"), `using Gua.Core; using Gua.Testing; class Program { ${methods}\nstatic void Main() { ${directChecks} } }`);
      const process = Bun.spawn(["dotnet", "run", "--project", join(directory, "Generated.csproj"), "--configuration", "Release"], { stdout: "pipe", stderr: "pipe" });
      const [stdout, stderr, exitCode] = await Promise.all([new Response(process.stdout).text(), new Response(process.stderr).text(), process.exited]);
      expect({ exitCode, diagnostics: exitCode === 0 ? "" : stdout + stderr }).toEqual({ exitCode: 0, diagnostics: "" });
    } finally { rmSync(directory, { recursive: true, force: true }); }
  }, 120_000);
});
