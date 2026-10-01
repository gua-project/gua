# Gua.Testing.Recording

`Gua.Testing.Recording` records semantic Gua actions and replays them through the
normal request-ID-correlated action lifecycle. The package targets both `net10.0`
and `netstandard2.1`. It depends on `Gua.Testing` but has
no PNG codec dependency.

## Record actions

`GuaRecorder` measures monotonic elapsed time, captures the semantic revision
before and after each completed action, and stores only the semantic target and
action arguments. Calls are serialized so one recording has deterministic order.

```csharp
using Gua.Testing.Recording;

var recorder = new GuaRecorder(host.Context);
await recorder.ClickAsync(new(Id: "open-login"));
await recorder.SetValueAsync(new(Id: "email"), "player@example.com",
    waitCondition: GuaWaitConditions.Visible("email"));
await recorder.SetValueAsync(new(Id: "password"), password,
    sensitive: true, secretKey: "login-password");
await recorder.PressKeyAsync("Enter");

GuaRecordingFile.Save("recordings/login.json", recorder.Recording);
```

Sensitive values are never stored. A sensitive `set_value` step contains only a
`secretKey`; replay requires the caller to resolve it.

## Replay reliably

Replay waits for the correlated host completion of every semantic action. It does
not treat queue acceptance as success and it does not consume unrelated events.
By default a recorded semantic wait condition replaces the corresponding delay;
steps without a condition retain their recorded delay.

```csharp
var recording = GuaRecordingFile.Load("recordings/login.json");
var result = await GuaReplayer.ReplayAsync(host.Context, recording, new()
{
    SecretResolver = key => key == "login-password" ? password : null,
    ActionTimeout = TimeSpan.FromSeconds(5),
});
```

Supported conditions are produced by `GuaWaitConditions`: visible, hidden,
enabled, disabled, focused, unfocused, checked, unchecked, text, and value.
Coordinate fallback is disabled by default and requires both explicit permission
and a caller-provided executor.

## Import retained diagnostics

`GuaRecordingFile.ImportDiagnostics` pairs enqueued operations with observed events
by request ID. Current runtimes provide monotonic elapsed milliseconds, per-entry
revision, scroll deltas, checked values, key/modifier data, and scroll units. The
returned metadata reports unpaired operations and whether an older diagnostics
payload forced synthetic sequence-based timing. Use `FromDiagnostics` only when
that import metadata is not needed.

Recording version 1 follows `protocol/schema/recording.schema.json`.
# Trace integration

`GuaRecordingTrace.Attach(trace, stepId, recorder.Recording, profile)` validates and
attaches an explicitly created recording. Save/Load and replay behavior are unchanged.
Sensitive steps are masked in Trace; safe secret references and decimal-string request
IDs are kept in a separate `gua.recording.references.v1` attachment. Correlation uses
the related Trace step's confirmed source/epoch, never a guessed Recording epoch.
Trace attachments are evidence, not a guarantee of replayability. Rejected, timed-out,
and interrupted operations remain Trace events without fabricated Recording steps.
