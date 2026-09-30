import { useCallback, useEffect, useRef, useState } from "react";
import { GuaTraceFileViewer } from "./TraceViewer";
import { ObservePanel } from "./ObservePanel";
import { NodeCodePanel } from "./NodeCodePanel";

import {
  type GuaInspectorClient,
  type GuaClockStatus,
  type GuaGameInputActions,
  type GuaGameInputActionSelector,
  type GuaGameInputState,
  type GameInputCommandInput,
  type GuaNode,
  type GuaWorldObject,
  type InspectorSnapshot,
  type InspectorState,
  MockInspectorClient,
  WebSocketInspectorClient,
  createCoalescedAsyncRunner,
  createInspectorState,
  findGameInputActionsCompatible,
  formatBounds,
  getSelectedNode,
  hasCompleteBounds,
  readSnapshot,
  selectNode,
  updateInspectorState,
  worldObjectDepths,
} from "./core";
import {
  InspectorRecorder,
  type BrowserVisualResult,
  type GuaRecording,
  type SemanticActionInput,
  compareImages,
  prepareManualGameInput,
  replayRecording,
  validateRecording,
} from "./automation";

export interface GuaInspectorAppProps {
  client?: GuaInspectorClient;
}

const defaultWebSocketUrl = "ws://127.0.0.1:8765";

export function GuaInspectorApp({ client }: GuaInspectorAppProps) {
  const [inspectorClient, setInspectorClient] = useState<GuaInspectorClient>(() => client ?? new MockInspectorClient());
  const [clientLabel, setClientLabel] = useState(() => client === undefined ? "Mock runtime" : "Custom client");
  const [webSocketUrl, setWebSocketUrl] = useState(() => window.localStorage.getItem("gua.inspector.wsUrl") ?? defaultWebSocketUrl);
  const [state, setState] = useState<InspectorState>(() => createInspectorState());
  const [status, setStatus] = useState<"idle" | "refreshing" | "error">("idle");
  const [error, setError] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const recorder = useRef(new InspectorRecorder());
  const [recording, setRecording] = useState(false);
  const [lastRecording, setLastRecording] = useState<GuaRecording | null>(null);
  const [baselineDataUri, setBaselineDataUri] = useState<string | null>(null);
  const [visualResult, setVisualResult] = useState<BrowserVisualResult | null>(null);
  const [secretsJson, setSecretsJson] = useState("{}");
  const [clock, setClock] = useState<GuaClockStatus | null>(null);
  const [gameInputActions, setGameInputActions] = useState<GuaGameInputActions | null>(null);
  const [gameInputState, setGameInputState] = useState<GuaGameInputState | null>(null);
  const clockRefresh = useRef<{
    client: GuaInspectorClient;
    run: () => Promise<void>;
  } | null>(null);

  useEffect(() => {
    if (client !== undefined) {
      setInspectorClient(client);
      setClientLabel("Custom client");
    }
  }, [client]);

  const refreshClock = useCallback(() => {
    let entry = clockRefresh.current;
    if (entry?.client !== inspectorClient) {
      const nextEntry: NonNullable<typeof clockRefresh.current> = {
        client: inspectorClient,
        run: async () => undefined,
      };
      nextEntry.run = createCoalescedAsyncRunner(async () => {
        try {
          const nextClock = await inspectorClient.getClock();
          if (clockRefresh.current === nextEntry) setClock(nextClock);
        } catch {
          if (clockRefresh.current === nextEntry) setClock(null);
        }
      });
      clockRefresh.current = nextEntry;
      entry = nextEntry;
    }
    return entry.run();
  }, [inspectorClient]);

  const refresh = useCallback(async () => {
    setStatus("refreshing");
    setError(null);
    try {
      const snapshot = await readSnapshot(inspectorClient);
      setState((current) => updateInspectorState(current, snapshot));
      await refreshClock();
      try {
        const [actions, inputState] = await Promise.all([findGameInputActionsCompatible(inspectorClient, { limit: 20 }), inspectorClient.getGameInputState()]);
        setGameInputActions(actions); setGameInputState(inputState);
      } catch { setGameInputActions(null); setGameInputState(null); }
      setStatus("idle");
    } catch (caught) {
      setError((caught as Error).message);
      setStatus("error");
    }
  }, [inspectorClient, refreshClock]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const maybeSubscribable = inspectorClient as GuaInspectorClient & {
      subscribeSnapshots?: (listener: (snapshot: InspectorSnapshot) => void) => () => void;
    };

    const unsubscribe = maybeSubscribable.subscribeSnapshots?.((snapshot) => {
      setState((current) => updateInspectorState(current, snapshot));
      void refreshClock();
      setStatus("idle");
      setError(null);
    });

    return () => {
      if (clockRefresh.current?.client === inspectorClient) clockRefresh.current = null;
      unsubscribe?.();
    };
  }, [inspectorClient, refreshClock]);

  useEffect(() => {
    if (!autoRefresh) {
      return undefined;
    }

    const intervalId = window.setInterval(() => {
      void refresh();
    }, 500);

    return () => window.clearInterval(intervalId);
  }, [autoRefresh, refresh]);

  const connectMock = () => {
    closeClient(inspectorClient);
    setState(createInspectorState());
    setClock(null);
    setError(null);
    setClientLabel("Mock runtime");
    setInspectorClient(new MockInspectorClient());
  };

  const connectWebSocket = () => {
    closeClient(inspectorClient);
    window.localStorage.setItem("gua.inspector.wsUrl", webSocketUrl);
    setState(createInspectorState());
    setClock(null);
    setError(null);
    setClientLabel(webSocketUrl);
    setInspectorClient(new WebSocketInspectorClient(webSocketUrl));
  };

  const selectedNode = getSelectedNode(state);

  const performAction = async (action: SemanticActionInput) => {
    const before = await inspectorClient.getUiTree();
    const outcome = await inspectorClient.performAction(action);
    const after = await inspectorClient.getUiTree();
    recorder.current.record(action, outcome, before.revision, outcome.completion?.revision ?? after.revision);
    await refresh();
    return outcome;
  };

  const clickSelected = async () => {
    if (selectedNode === null || !selectedNode.actions.includes("click")) {
      return;
    }

    await performAction({ action: "click", nodeId: selectedNode.id });
  };

  const focusSelected = async () => {
    if (selectedNode === null || !selectedNode.actions.includes("focus")) {
      return;
    }

    await performAction({ action: "focus", nodeId: selectedNode.id });
  };

  const startRecording = () => {
    try {
      recorder.current.start();
      setRecording(true);
      setError(null);
    } catch (caught) { setError((caught as Error).message); }
  };

  const stopRecording = () => {
    try {
      setLastRecording(recorder.current.stop());
      setRecording(false);
      setError(null);
    } catch (caught) { setError((caught as Error).message); }
  };

  const replay = async () => {
    if (lastRecording === null) return;
    try {
      const parsedSecrets = JSON.parse(secretsJson) as unknown;
      if (typeof parsedSecrets !== "object" || parsedSecrets === null || Array.isArray(parsedSecrets)) {
        throw new Error("Secrets must be a JSON object.");
      }
      await replayRecording(
        lastRecording,
        () => inspectorClient.getUiTree(),
        (action) => inspectorClient.performAction(action),
        parsedSecrets as Record<string, string>,
        (command) => inspectorClient.performGameInput(command),
        (actionId) => findGameInputActionsCompatible(inspectorClient, { id: actionId, limit: 1 }),
        (action) => window.confirm(`Action '${action.id}' (${action.risk}) requires confirmation. Continue?`),
      );
      await refresh();
      setError(null);
    } catch (caught) { setError((caught as Error).message); }
  };

  const compare = async () => {
    if (baselineDataUri === null || state.screenshot.dataUri.length === 0) return;
    try {
      setVisualResult(await compareImages(baselineDataUri, state.screenshot.dataUri));
      setError(null);
    } catch (caught) { setError((caught as Error).message); }
  };

  return (
    <div className="gua-app">
      <header className="gua-topbar">
        <div>
          <div className="gua-brand">Gua Inspector</div>
          <div className="gua-screen">{state.uiTree.screen} · {clientLabel}</div>
        </div>
        <div className="gua-topbar__actions">
          {client === undefined ? (
            <form
              className="gua-connect"
              onSubmit={(event) => {
                event.preventDefault();
                connectWebSocket();
              }}
            >
              <input
                aria-label="Gua WebSocket bridge URL"
                value={webSocketUrl}
                onChange={(event) => setWebSocketUrl(event.currentTarget.value)}
              />
              <button type="submit">Connect</button>
              <button type="button" onClick={connectMock}>Mock</button>
            </form>
          ) : null}
          <label className="gua-toggle">
            <input
              type="checkbox"
              checked={autoRefresh}
              onChange={(event) => setAutoRefresh(event.currentTarget.checked)}
            />
            <span>Poll</span>
          </label>
          {error !== null ? <span className="gua-error">{error}</span> : null}
          <button type="button" onClick={() => void refresh()} disabled={status === "refreshing"}>
            {status === "refreshing" ? "Refreshing" : "Refresh"}
          </button>
        </div>
      </header>

      <details style={{ padding: 16 }}><summary>Open Gua Trace (offline)</summary><GuaTraceFileViewer /></details>
      <main className="gua-layout">
        <TreePanel
          nodes={state.uiTree.nodes}
          selectedNodeId={state.selectedNodeId}
          onSelect={(nodeId) => setState((current) => selectNode(current, nodeId))}
        />
        <WorldTreePanel objects={state.worldObjectTree.objects} scene={state.worldObjectTree.scene} />
        <ObservePanel client={inspectorClient} />
        <NodeDetailPanel
          key={JSON.stringify([selectedNode?.id, selectedNode?.actions.includes("set_value")])}
          node={selectedNode}
          onClick={() => void clickSelected()}
          onFocus={() => void focusSelected()}
          onAction={(action) => void performAction(action)}
        />
        <ScreenshotPanel screenshot={state.screenshot} selectedNode={selectedNode} />
        <LogPanel logs={state.logs} />
        <ClockPanel
          clock={clock}
          onInstall={async (step) => setClock(await inspectorClient.installClock(0, step))}
          onPause={async () => setClock(await inspectorClient.pauseClock())}
          onRunFor={async (duration, step) => { setClock(await inspectorClient.runClockFor(duration, step)); await refresh(); }}
          onResume={async () => setClock(await inspectorClient.resumeClock())}
          onError={(message) => setError(message)}
        />
        <GameInputPanel
          actions={gameInputActions}
          state={gameInputState}
          onSearch={async (selector) => setGameInputActions(await findGameInputActionsCompatible(inspectorClient, selector))}
          onInput={async (command) => {
            const currentCommand = await prepareManualGameInput(
              command,
              (actionId) => findGameInputActionsCompatible(inspectorClient, { id: actionId, limit: 1 }),
              (action) => window.confirm(`Action '${action.id}' (${action.risk}) requires confirmation. Continue?`),
            );
            if (currentCommand === null) return;
            const receipt = await inspectorClient.performGameInput(currentCommand);
            recorder.current.recordGameInput(currentCommand, receipt.requestId);
            setGameInputState(await inspectorClient.getGameInputState());
          }}
          onError={(message) => setError(message)}
        />
        <AutomationPanel
          recording={recording}
          lastRecording={lastRecording}
          screenshotDataUri={state.screenshot.dataUri}
          baselineDataUri={baselineDataUri}
          visualResult={visualResult}
          secretsJson={secretsJson}
          onSecretsJson={setSecretsJson}
          onStart={startRecording}
          onStop={stopRecording}
          onReplay={() => void replay()}
          onDownloadRecording={() => lastRecording !== null && downloadText("gua-recording.json", JSON.stringify(lastRecording, null, 2), "application/json")}
          onImportRecording={(text) => {
            try {
              const value = JSON.parse(text) as unknown;
              validateRecording(value);
              setLastRecording(value);
              setError(null);
            }
            catch (caught) { setError((caught as Error).message); }
          }}
          onUseCurrentBaseline={() => { setBaselineDataUri(state.screenshot.dataUri); setVisualResult(null); }}
          onBaseline={setBaselineDataUri}
          onCompare={() => void compare()}
        />
      </main>
    </div>
  );
}

function GameInputPanel({ actions, state, onSearch, onInput, onError }: {
  actions: GuaGameInputActions | null; state: GuaGameInputState | null;
  onSearch(selector: GuaGameInputActionSelector): Promise<void>;
  onInput(command: GameInputCommandInput): Promise<void>; onError(message: string): void;
}) {
  const [rawCode, setRawCode] = useState("Space");
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("");
  const [tags, setTags] = useState("");
  const [valueType, setValueType] = useState("");
  const [activeOnly, setActiveOnly] = useState(true);
  const run = (command: GameInputCommandInput) => {
    void onInput(command).catch((caught) => onError((caught as Error).message));
  };
  return <section className="gua-panel gua-game-input-panel">
    <PanelHeader title="Game Input" detail={actions === null ? "unsupported" : `${actions.context} · r${actions.revision}`} />
    <form className="gua-game-action-search" onSubmit={(event) => {
      event.preventDefault();
      void onSearch({ query: query || undefined, category: category || undefined,
        tags: tags ? tags.split(",").map((tag) => tag.trim()).filter(Boolean) : undefined,
        valueType: valueType ? valueType as GuaGameInputActionSelector["valueType"] : undefined,
        active: activeOnly ? true : undefined, limit: 20 }).catch((caught) => onError((caught as Error).message));
    }}>
      <input aria-label="Search game actions" placeholder="Search actions" value={query} onChange={(event) => setQuery(event.currentTarget.value)} />
      <input aria-label="Game action category" placeholder="Category" value={category} onChange={(event) => setCategory(event.currentTarget.value)} />
      <input aria-label="Game action tags" placeholder="Tags (comma separated)" value={tags} onChange={(event) => setTags(event.currentTarget.value)} />
      <select aria-label="Game action value type" value={valueType} onChange={(event) => setValueType(event.currentTarget.value)}>
        <option value="">Any type</option><option value="button">button</option><option value="axis1d">axis1d</option>
        <option value="vector2">vector2</option><option value="text">text</option>
      </select>
      <label><input type="checkbox" checked={activeOnly} onChange={(event) => setActiveOnly(event.currentTarget.checked)} />Active</label>
      <button type="submit">Find</button>
    </form>
    {actions?.actions.map((action) => <div className="gua-game-action" key={action.id}>
      <span><strong>{action.id}</strong><small>{action.valueType} · {action.active ? "active" : "inactive"} · {action.risk}</small></span>
      {action.valueType === "button" ? <>
        <button type="button" disabled={!action.active} onClick={() => run({ type: "press_game_input_action", actionId: action.id })}>Press</button>
        {action.holdable ? <button type="button" disabled={!action.active} onClick={() => run({ type: "set_game_input_action", actionId: action.id, value: true })}>Hold</button> : null}
        <button type="button" onClick={() => run({ type: "release_game_input_action", actionId: action.id })}>Release</button>
      </> : <GameInputValueControl action={action} onRun={(value) => run({ type: "set_game_input_action", actionId: action.id, value })} />}
    </div>)}
    <div className="gua-game-raw">
      <input aria-label="W3C physical key code" value={rawCode} onChange={(event) => setRawCode(event.currentTarget.value)} />
      <button type="button" onClick={() => run({ type: "press_physical_key", code: rawCode })}>Press physical key</button>
      <button type="button" className="gua-danger" onClick={() => run({ type: "release_all_game_inputs" })}>Release all</button>
    </div>
    <ul className="gua-held-inputs">{state?.held.map((held, index) => <li key={`${held.kind}:${held.target}:${held.deviceIndex}:${index}`}>{held.target} · {held.remainingLeaseMs.toFixed(0)} ms</li>)}</ul>
  </section>;
}

function GameInputValueControl({ action, onRun }: { action: NonNullable<GuaGameInputActions>["actions"][number]; onRun(value: unknown): void }) {
  const [value, setValue] = useState(action.valueType === "vector2" ? "0,0" : action.valueType === "text" ? "" : "0");
  return <><input aria-label={`${action.id} value`} value={value} onChange={(event) => setValue(event.currentTarget.value)} />
    <button type="button" disabled={!action.active} onClick={() => onRun(action.valueType === "vector2" ? { x: Number(value.split(",")[0]), y: Number(value.split(",")[1]) } : action.valueType === "axis1d" ? Number(value) : value)}>Set</button></>;
}

function ClockPanel({ clock, onInstall, onPause, onRunFor, onResume, onError }: {
  clock: GuaClockStatus | null;
  onInstall(step?: number): Promise<void>; onPause(): Promise<void>;
  onRunFor(duration: number, step?: number): Promise<void>; onResume(): Promise<void>;
  onError(message: string): void;
}) {
  const [duration, setDuration] = useState("1000");
  const [step, setStep] = useState("");
  const parsedStep = step === "" ? undefined : Number(step);
  const run = (action: () => Promise<void>) => void action().catch((caught) => onError((caught as Error).message));
  return <section className="gua-panel gua-clock-panel">
    <PanelHeader title="Virtual Clock" detail={clock === null ? "unsupported" : `${clock.state} · ${clock.nowMs.toFixed(2)} ms`} />
    <div className="gua-clock-controls">
      <input aria-label="Clock duration milliseconds" type="number" min="0" value={duration} onChange={(e) => setDuration(e.currentTarget.value)} />
      <input aria-label="Clock step milliseconds" type="number" min="0.001" placeholder="default step" value={step} onChange={(e) => setStep(e.currentTarget.value)} />
      <button type="button" disabled={clock === null || clock.installed} onClick={() => run(() => onInstall(parsedStep))}>Install</button>
      <button type="button" disabled={clock === null || !clock.installed || clock.state === "paused"} onClick={() => run(onPause)}>Pause</button>
      <button type="button" disabled={clock === null || clock.state !== "paused" || Number(duration) < 0} onClick={() => run(() => onRunFor(Number(duration), parsedStep))}>Run for</button>
      <button type="button" disabled={clock === null || clock.state !== "paused"} onClick={() => run(onResume)}>Resume</button>
    </div>
  </section>;
}

function closeClient(client: GuaInspectorClient): void {
  const maybeClosable = client as GuaInspectorClient & { close?: () => void };
  maybeClosable.close?.();
}

interface TreePanelProps {
  nodes: GuaNode[];
  selectedNodeId: string | null;
  onSelect(nodeId: string): void;
}

function TreePanel({ nodes, selectedNodeId, onSelect }: TreePanelProps) {
  return (
    <section className="gua-panel gua-tree-panel">
      <PanelHeader title="UI Tree" detail={`${nodes.length} nodes`} />
      <ol className="gua-tree">
        {nodes.map((node) => (
          <li key={node.id}>
            <button
              type="button"
              className="gua-tree__node"
              data-depth={node.parentId === undefined ? 0 : 1}
              aria-selected={node.id === selectedNodeId}
              onClick={() => onSelect(node.id)}
            >
              <span className="gua-role">{node.role}</span>
              <span className="gua-tree__label">
                <span>{node.label || node.id}</span>
                <small>#{node.id}</small>
              </span>
              <NodeFlags node={node} />
            </button>
          </li>
        ))}
      </ol>
    </section>
  );
}

function WorldTreePanel({ objects, scene }: { objects: GuaWorldObject[]; scene: string }) {
  const depths = worldObjectDepths(objects);
  return (
    <section className="gua-panel gua-tree-panel gua-world-panel">
      <PanelHeader title="World Object Tree" detail={`${objects.length} objects · ${scene}`} />
      <ol className="gua-tree">
        {objects.map((object) => { const depth = depths.get(object.id) ?? 0; return <li key={object.id}><div className="gua-tree__node" data-depth={depth} style={{ marginInlineStart: depth * 14 }}>
          <span className="gua-role">{object.kind}</span><span className="gua-tree__label"><span>{object.label ?? "[label omitted]"}</span><small>#{object.id} · {object.space} ({object.position.x ?? "?"}, {object.position.y ?? "?"}{object.position.z === undefined ? "" : `, ${object.position.z}`})</small></span>
          <span>{object.visibleToPlayer ? "visible" : "hidden"}{object.active ? "" : " · inactive"}</span>
        </div></li>; })}
      </ol>
    </section>
  );
}

interface NodeDetailPanelProps {
  node: GuaNode | null;
  onClick(): void;
  onFocus(): void;
  onAction(action: SemanticActionInput): void;
}

function NodeDetailPanel({ node, onClick, onFocus, onAction }: NodeDetailPanelProps) {
  const [value, setValue] = useState("");
  const [key, setKey] = useState("Enter");
  const [sensitive, setSensitive] = useState(false);
  const [secretKey, setSecretKey] = useState("");
  if (node === null) {
    return (
      <section className="gua-panel gua-detail-panel">
        <PanelHeader title="Node Detail" />
        <p className="gua-muted">No node selected.</p>
      </section>
    );
  }

  return (
    <section className="gua-panel gua-detail-panel">
      <PanelHeader title="Node Detail" detail={node.id} />
      <NodeCodePanel key={`${node.id}:${sensitive}`} node={node} sensitive={sensitive} />
      <div className="gua-command-row">
        <button type="button" onClick={onClick} disabled={!node.actions.includes("click")}>
          Click
        </button>
        <button type="button" onClick={onFocus} disabled={!node.actions.includes("focus")}>
          Focus
        </button>
      </div>
      <div className="gua-action-editor">
        {node.actions.includes("set_value") || node.actions.includes("select") ? (
          <>
            <input aria-label="Action value" value={value} onChange={(event) => setValue(event.currentTarget.value)} placeholder="value" />
            {node.actions.includes("set_value") ? (
              <button
                type="button"
                onClick={() => onAction({ action: "set_value", nodeId: node.id, value, sensitive, secretKey: sensitive ? secretKey : undefined })}
                disabled={sensitive && secretKey.length === 0}
              >Set value</button>
            ) : null}
            {node.actions.includes("select") ? <button type="button" onClick={() => onAction({ action: "select", nodeId: node.id, value })}>Select</button> : null}
            {node.actions.includes("set_value") ? (
              <label><input type="checkbox" checked={sensitive} onChange={(event) => setSensitive(event.currentTarget.checked)} /> Sensitive</label>
            ) : null}
            {sensitive ? <input aria-label="Secret key" value={secretKey} onChange={(event) => setSecretKey(event.currentTarget.value)} placeholder="secret key" /> : null}
          </>
        ) : null}
        {node.actions.includes("set_checked") ? (
          <div className="gua-command-row">
            <button type="button" onClick={() => onAction({ action: "set_checked", nodeId: node.id, checked: true })}>Check</button>
            <button type="button" onClick={() => onAction({ action: "set_checked", nodeId: node.id, checked: false })}>Uncheck</button>
          </div>
        ) : null}
        {node.actions.includes("scroll") ? (
          <div className="gua-command-row">
            <button type="button" onClick={() => onAction({ action: "scroll", nodeId: node.id, deltaX: 0, deltaY: -1, scrollUnit: 1 })}>Scroll up</button>
            <button type="button" onClick={() => onAction({ action: "scroll", nodeId: node.id, deltaX: 0, deltaY: 1, scrollUnit: 1 })}>Scroll down</button>
          </div>
        ) : null}
        {node.actions.includes("press_key") ? (
          <div className="gua-command-row">
            <input aria-label="Key name" value={key} onChange={(event) => setKey(event.currentTarget.value)} />
            <button type="button" onClick={() => onAction({ action: "press_key", nodeId: node.id, key })} disabled={key.length === 0}>Press key</button>
          </div>
        ) : null}
      </div>
      <table className="gua-detail">
        <tbody>
          <DetailRow name="id" value={node.id} />
          <DetailRow name="role" value={node.role} />
          <DetailRow name="label" value={node.label ?? ""} />
          <DetailRow name="parent" value={node.parentId ?? ""} />
          <DetailRow name="visible" value={String(node.visible)} />
          <DetailRow name="enabled" value={String(node.enabled)} />
          <DetailRow name="bounds" value={formatBounds(node.bounds)} />
          <DetailRow name="state" value={JSON.stringify(node.state ?? {})} />
          <DetailRow name="actions" value={node.actions.join(", ")} />
        </tbody>
      </table>
    </section>
  );
}

interface ScreenshotPanelProps {
  screenshot: {
    dataUri: string;
    width: number;
    height: number;
  };
  selectedNode: GuaNode | null;
}

function ScreenshotPanel({ screenshot, selectedNode }: ScreenshotPanelProps) {
  const hasImage = screenshot.dataUri.length > 0;
  const boxStyle = selectedNode === null || !hasCompleteBounds(selectedNode.bounds) ||
      screenshot.width <= 0 || screenshot.height <= 0
    ? undefined
    : {
        left: `${(selectedNode.bounds.x / screenshot.width) * 100}%`,
        top: `${(selectedNode.bounds.y / screenshot.height) * 100}%`,
        width: `${(selectedNode.bounds.w / screenshot.width) * 100}%`,
        height: `${(selectedNode.bounds.h / screenshot.height) * 100}%`,
      };

  return (
    <section className="gua-panel gua-screenshot-panel">
      <PanelHeader
        title="Screenshot"
        detail={screenshot.width > 0 && screenshot.height > 0 ? `${screenshot.width} x ${screenshot.height}` : undefined}
      />
      <div className="gua-screenshot">
        {hasImage ? (
          <div className="gua-screenshot__stage">
            <img src={screenshot.dataUri} width={screenshot.width} height={screenshot.height} alt="Gua runtime screenshot" />
            {boxStyle !== undefined ? <span className="gua-bounds" style={boxStyle} /> : null}
          </div>
        ) : (
          <div className="gua-placeholder">
            <strong>No screenshot captured</strong>
            <span>Runtime adapters can provide a data URI for this panel.</span>
          </div>
        )}
      </div>
    </section>
  );
}

function LogPanel({ logs }: { logs: Array<{ sequence: number; level: string; message: string }> }) {
  return (
    <section className="gua-panel gua-log-panel">
      <PanelHeader title="Logs" detail={`${logs.length} entries`} />
      <div className="gua-log-list">
        {logs.length === 0 ? (
          <p className="gua-muted">No logs.</p>
        ) : (
          logs.map((entry) => (
            <div className="gua-log" key={entry.sequence}>
              <span className="gua-muted">{entry.sequence}</span>
              <span className={`gua-log__level gua-log__level--${entry.level}`}>{entry.level}</span>
              <span>{entry.message}</span>
            </div>
          ))
        )}
      </div>
    </section>
  );
}

interface AutomationPanelProps {
  recording: boolean;
  lastRecording: GuaRecording | null;
  screenshotDataUri: string;
  baselineDataUri: string | null;
  visualResult: BrowserVisualResult | null;
  secretsJson: string;
  onSecretsJson(value: string): void;
  onStart(): void;
  onStop(): void;
  onReplay(): void;
  onDownloadRecording(): void;
  onImportRecording(text: string): void;
  onUseCurrentBaseline(): void;
  onBaseline(dataUri: string): void;
  onCompare(): void;
}

function AutomationPanel(props: AutomationPanelProps) {
  return (
    <section className="gua-panel gua-automation-panel">
      <PanelHeader title="Automation" detail={props.recording ? "recording" : `${props.lastRecording?.steps.length ?? 0} steps`} />
      <div className="gua-automation-grid">
        <div>
          <h3>Recording / Replay</h3>
          <div className="gua-command-row">
            <button type="button" onClick={props.onStart} disabled={props.recording}>Start recording</button>
            <button type="button" onClick={props.onStop} disabled={!props.recording}>Stop</button>
            <button type="button" onClick={props.onReplay} disabled={props.lastRecording === null || props.recording}>Replay</button>
            <button type="button" onClick={props.onDownloadRecording} disabled={props.lastRecording === null}>Download JSON</button>
          </div>
          <label className="gua-file-field">
            <span>Import recording JSON</span>
            <input
              type="file"
              accept="application/json,.json"
              onChange={(event) => {
                const file = event.currentTarget.files?.[0];
                if (file !== undefined) void file.text().then(props.onImportRecording);
              }}
            />
          </label>
          <label className="gua-file-field">
            <span>Replay secrets (kept in memory only)</span>
            <textarea value={props.secretsJson} onChange={(event) => props.onSecretsJson(event.currentTarget.value)} rows={3} />
          </label>
        </div>
        <div>
          <h3>Visual comparison</h3>
          <div className="gua-command-row">
            <button type="button" onClick={props.onUseCurrentBaseline} disabled={props.screenshotDataUri.length === 0}>
              Use current as baseline
            </button>
            <button type="button" onClick={props.onCompare} disabled={props.baselineDataUri === null || props.screenshotDataUri.length === 0}>
              Compare
            </button>
          </div>
          <label className="gua-file-field">
            <span>Choose baseline image</span>
            <input
              type="file"
              accept="image/png,image/*"
              onChange={(event) => {
                const file = event.currentTarget.files?.[0];
                if (file !== undefined) void readFileAsDataUri(file).then(props.onBaseline);
              }}
            />
          </label>
          {props.visualResult !== null ? (
            <div className={props.visualResult.matched ? "gua-visual-result gua-visual-result--matched" : "gua-visual-result gua-visual-result--failed"}>
              <strong>{props.visualResult.matched ? "Matched" : `Failed: ${props.visualResult.reason}`}</strong>
              <span>{props.visualResult.differentPixels} / {props.visualResult.comparedPixels} pixels ({(props.visualResult.differentPixelRatio * 100).toFixed(4)}%)</span>
              <div className="gua-command-row">
                <button type="button" onClick={() => downloadDataUri("actual.png", props.visualResult?.actualDataUri as string)}>Actual</button>
                <button type="button" onClick={() => downloadDataUri("expected.png", props.visualResult?.expectedDataUri as string)}>Expected</button>
                <button type="button" onClick={() => downloadDataUri("diff.png", props.visualResult?.diffDataUri as string)}>Diff</button>
                <button type="button" onClick={() => downloadText("comparison.json", props.visualResult?.comparisonJson as string, "application/json")}>Manifest</button>
              </div>
            </div>
          ) : null}
        </div>
      </div>
    </section>
  );
}

function readFileAsDataUri(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => typeof reader.result === "string" ? resolve(reader.result) : reject(new Error("Image could not be read."));
    reader.onerror = () => reject(reader.error ?? new Error("Image could not be read."));
    reader.readAsDataURL(file);
  });
}

function downloadText(name: string, text: string, type: string): void {
  const url = URL.createObjectURL(new Blob([text], { type }));
  clickDownload(name, url);
  URL.revokeObjectURL(url);
}

function downloadDataUri(name: string, dataUri: string): void { clickDownload(name, dataUri); }

function clickDownload(name: string, href: string): void {
  const anchor = document.createElement("a");
  anchor.download = name;
  anchor.href = href;
  anchor.click();
}

function PanelHeader({ title, detail }: { title: string; detail?: string }) {
  return (
    <div className="gua-panel__header">
      <h2>{title}</h2>
      {detail !== undefined ? <span>{detail}</span> : null}
    </div>
  );
}

function DetailRow({ name, value }: { name: string; value: string }) {
  return (
    <tr>
      <th>{name}</th>
      <td>{value}</td>
    </tr>
  );
}

function NodeFlags({ node }: { node: GuaNode }) {
  return (
    <span className="gua-flags">
      {!node.visible ? <span>hidden</span> : null}
      {!node.enabled ? <span>disabled</span> : null}
      {node.state?.focused === true ? <span>focused</span> : null}
    </span>
  );
}
