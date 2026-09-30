import { useRef, useState } from "react";
import type { GuaNode } from "./core";
import { copyGeneratedCode, generateNodeCode } from "./codegen";

export function NodeCodePanel({ node, sensitive }: { node: GuaNode; sensitive: boolean }) {
  const [locatorId, setLocatorId] = useState("id");
  const [actionId, setActionId] = useState("");
  const [stateId, setStateId] = useState("visible");
  const [code, setCode] = useState("");
  const [message, setMessage] = useState("");
  const field = useRef<HTMLTextAreaElement>(null);
  const generated = generateNodeCode(node, locatorId, sensitive);
  const locator = generated.locators.find((choice) => choice.id === locatorId) ?? generated.locators[0]!;
  const action = generated.actions.find((choice) => choice.id === actionId) ?? generated.actions[0];
  const state = generated.states.find((choice) => choice.id === stateId) ?? generated.states[0]!;
  const copySequence = useRef(0);
  const copy = async (source: string) => {
    const sequence = ++copySequence.current;
    setCode(source);
    setMessage("Copying C# code…");
    const result = await copyGeneratedCode(source, (text) => navigator.clipboard.writeText(text));
    if (sequence !== copySequence.current) return;
    setMessage(result);
    field.current?.focus();
    field.current?.select();
  };
  return <div className="gua-code-panel">
    <h3>C# test code</h3>
    <p>Requires Gua.Testing and an IGuaContext named context. Verify ID lifetime and unique matches. Action arguments are examples; waits use the observed state and resolved ID.</p>
    <label>Locator <select aria-label="C# locator" value={locator.id} onChange={(event) => setLocatorId(event.currentTarget.value)}>
      {generated.locators.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
    </select></label>
    <pre>{locator.code}</pre>
    <button type="button" onClick={() => void copy(locator.code)}>Copy locator</button>
    {action && <div><label>Action <select aria-label="C# action" value={action.id} onChange={(event) => setActionId(event.currentTarget.value)}>
      {generated.actions.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
    </select></label><button type="button" onClick={() => void copy(action.code)}>Copy action</button></div>}
    <label>Observed state <select aria-label="C# observed state" value={state.id} onChange={(event) => setStateId(event.currentTarget.value)}>
      {generated.states.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
    </select></label>
    <button type="button" onClick={() => void copy(state.code)}>Copy wait/assertion</button>
    <textarea ref={field} aria-label="Generated C# code" readOnly value={code} rows={8} onFocus={(event) => event.currentTarget.select()} />
    <p role="status">{message}</p>
  </div>;
}
