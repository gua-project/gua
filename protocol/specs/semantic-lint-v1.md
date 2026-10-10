# Semantic Lint v1

## 公開されたUI/Worldの矛盾を調べるために

「クリックできるボタンなのに名前がない」「World objectが参照するUIが見つからない」
といった公開データの問題を、要求したときに一覧にする診断です。
Lintは実際の見た目やゲームの正しさを採点するものではありません。

「確定したツリーを取得 → 指定profileの公開範囲でルール検査 → ruleIdとpathを持つreport」
の順で動きます。stagingはまだ公開していないフレームで、検査対象にしません。
保存済みsnapshotを使う場合は、呼び出し側が正しい公開範囲のJSONを用意します。
Lintは登録・公開の途中に自動実行されず、修正やゲーム操作も行いません。

[GuaSemanticLinter.Analyze](../../bindings/dotnet/src/Gua.Core/GuaSemanticLint.cs)から
[nativeルール実装](../../native/gua-core/src/semantic_lint.cpp)を追ってください。
[SemanticLintTests](../../bindings/dotnet/tests/Gua.Selector.Tests/SemanticLintTests.cs)は公開済みprojectionとfixtureの確認箇所です。
実行準備は[開発者ガイドのローカル確認手順](../../docs/developer-reading-guide.ja.md)、判定基準は以下のrule表です。

`semantic-lint.schema.json` describes an explicit, read-only analysis of published
UI v2 and optional World v1 snapshots. The native core owns all rules. Nothing
calls lint during registration, publication, observation or action processing.
Publication validation, rejection and legacy readers remain unchanged.

`gua_semantic_lint_analyze` captures both committed trees under one core lock and
applies the existing Debug/Player projection before analysis. Staged frames are
never inspected. `include_world = 0` reports only UI. An empty committed tree is
valid, including before the first frame. Reports are immutable and caller-owned.

`gua_semantic_lint_analyze_snapshots` supports stored published snapshots and
fixtures from external publishers. It never publishes, projects, or accepts host
descriptors. The caller must supply the already projected source snapshots and
their actual profile. Paired snapshots must share a sessionEpoch.
Metadata uses the native unsigned 64-bit range. Integral decimal and exponent
representations are accepted and normalized without rounding; selection indices
are also compared exactly rather than converted to double.
Native World publication already rejects duplicate IDs, dangling parents and cycles; those
rules also cover stored observations without weakening publication validation.

The WebSocket command is `{ "id": 1, "type": "semantic_lint",
"includeWorld": true }` (`includeWorld` defaults to true). It always uses the
host profile, never a client override or client-supplied trees. Unknown fields
and invalid options are rejected. Unsupported bridges return an explicit error.
Reports carry source schema/session/frame/revision and screen/scene metadata,
not copied tree content. Findings are ordered by UI structural, UI semantic,
World structural, then World-to-UI reference checks, preserving source indices.
Paths address the source pair as `$.uiTree.nodes[i].field` or
`$.worldObjectTree.objects[i].field`.

| Rule ID | Severity | Condition / repair |
| --- | --- | --- |
| duplicate-id | error | Every occurrence of a duplicated ID; use unique IDs within each tree. |
| missing-parent | error | Nonempty parentId absent from its tree; repair the reference. |
| parent-cycle | error | Each member of an unambiguous parent cycle, including self cycles; remove the cycle. Duplicate-ID chains are not guessed. |
| broken-related-ui | error | Nonempty World relatedUiNodeId absent from the projected UI tree; repair the reference. |
| missing-accessible-name | warning | Interactive role or any advertised action with absent/ASCII-blank label; supply an accessible label. Text/value are not accessible-name substitutes. |
| role-action-contradiction | error | An advertised action not supported by the protocol's role/action matrix; remove it or correct the role. |
| disabled-actions | error | enabled=false with advertised actions; clear those actions. |
| role-state-contradiction | error | A known role-specific state on an incompatible role; correct role/state. |
| range-bounds | error | rangeMin > rangeMax; repair bounds. |
| range-value | error | Known rangeValue below known min or above known max; repair the value. |
| selection-order | error | selectionStart > selectionEnd; order endpoints. |
| selection-index | error | Fractional or negative text index, or selectedIndex < -1; repair index. |
| scroll-bounds | error | Negative known offset/maximum or offset > known maximum on either axis; repair scroll state. |

The action matrix matches the core's existing `supports_action` behavior.
Role-specific state: checked belongs to checkbox/radio; selected to listitem/tab;
caret/selection endpoints to textbox; range fields to slider; scroll fields to
list/scrollarea; selectedIndex to list/combobox/tablist. Present false values are
known values. Omitted state is unknown: no pair comparison requires an omitted
field, and no missing optional action/state is diagnosed. Interactive roles are
button, checkbox, radio, slider, textbox, list, listitem, menuitem, combobox,
tablist, tab and scrollarea.

Text-length and child-count index checks are deliberately excluded: publisher
index units and partial children are not a cross-language v1 contract. No
heuristics, secret inference, automatic fixes or severity threshold exist in the
engine. Callers filter findings or decide their own failure threshold.

Debug and Player use identical IDs and severities on their respective projected
data. Messages never embed field values or referenced IDs. Targets and metadata
come exclusively from the inspected projection; no Debug fallback is performed
for missing related UI nodes. Changing hidden content cannot change a Player
report, except changes already observable through the existing projection.

C++ `gua::SemanticLinter` wraps the C ABI. .NET
`GuaSemanticLinter.Analyze(context, options)` uses P/Invoke and returns a typed
report. The offline overload accepts stored UI and optional World JSON. Remote
`GuaWebSocketContext.AnalyzeSemanticLint()` obtains the host's native report.
Consumers accept additive report fields while requiring schemaVersion 1.
