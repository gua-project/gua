# Observe transport v1（G-03 / #120）

G-01/G-02のValue、Owner、sample、通知、gap、epoch、公開profile契約をそのまま使う。
OPEN-01は追加capability `observe_v1` と新コマンドで解消する。既存UI/World、
Observe v1 JSON、ABI構造体、version番号を変更しない。旧clientは既存応答を使用できる。
OPEN-03のSnapshot+cursorはnativeの原子的subscribeを使用する。切断後のcursor再開は
提供せず、再接続は新規subscribeする。gap/stale_sessionは正常空状態に変換しない。

## 着手時の対応表

| 経路 | profile | 登録と寿命 | 読取・通知 |
| --- | --- | --- | --- |
| C ABI / C++ | Debug / Player | G-02のOwner・登録token | native Snapshot / subscribe / poll |
| Gua.Runtime / .NET | Debug / Player | 同じnative context、host thread getter | 同じSnapshot / subscribe / poll |
| Godot GDScript addon | Debug / Player | 公開済みUI/ObjectにOwner、明示解除＋tree削除 | native runtime＋WebSocket |
| Unity desktop Mono | Debug / Player | 実Objectに保持したOwner、破棄でDispose＋tree削除 | Gua.Runtime＋WebSocket |
| WebSocket / .NET remote / Inspector / MCP | host設定profile | remote登録は禁止、購読は接続所有 | capability確認、新規Snapshot / subscribe / poll / unsubscribe |
| Godot Web / Unity WebGL / WebMCP | Player | engineの同じ登録 | same-page port、独立購読とdetach解放 |

Playerのprofileはhostが固定し、要求パラメータでDebugへ昇格させない。
transport clientの作成時もhostの上限を適用する。hostがDebugからPlayerへ降格した後は、
既存Debug clientの全commandを失効として拒否し、その購読を解放する。
購読上限は一接続64。未知・他接続のsubscriptionIdは安全な固定エラーとする。
subscribeの応答喪失・不正な成功応答・timeout・cancelでIDが不明な場合は、所有接続を閉じて購読を回収する。
再接続は可能だが既存tokenは復元しない。明示的なサーバー拒否では既存購読を維持する。
古い接続のtokenはpollを拒否し、Disposeは新しい接続へunsubscribeを送らない。
enum候補はValue生成時のcatalogの必要な型だけを所有コピーして運ぶ。
秘密値のcatalogを含めて除外する。候補定義を別途Observeへ再登録しない。

## 新コマンド

`get_observe_snapshot`、`subscribe_observations`、`poll_observations`、
`unsubscribe_observations`。後二者だけ正の安全整数`subscriptionId`が必須。
全commandの要求`id`も正の安全整数で、検証済みの値を応答へそのまま返す。
未知field、重複key、不正JSON、不正IDを拒否する。
subscribe応答は `{subscriptionId, snapshot}`。unsubscribe応答は `null`。
Snapshot/pollの新transport応答は `{document, catalogs}`。
`document`は変更しないObserve v1、`catalogs`はentries/eventsと同じ順の配列。
各要素のvalue/before/afterは、そのValueで使用するEnum Catalog v1の定義一つだけを含む。
非enum・取得不能の箇所は省略する。欠損時はevents/catalogsをともに空にする。
標準worldPositionはWorld Object TreeのruntimeIdで関連付け、追加phaseだけ登録する。
両方の取得は同時Snapshotとは限らず、sourceId/epoch/UI・World frame/revisionを保持する。

## 検証

実native登録→runtime→WebSocket→実.NET clientで型・候補、同名Owner、変更、
getter失敗、gap、reset、他接続の拒否、Player秘密値を検証する。
実エンジンの検証結果、Web export、他OS、remote CIの未実行は引き渡しで区別する。

## ホストでの登録例

登録はUI/Worldの最初の公開後に行う。標準位置はWorld treeの`position`を読み、
追加値の`runtimeId`で同じObjectに関連付ける。登録tokenとOwnerをObjectの寿命中保持する。

```csharp
// GuaRuntimeで最初のWorld frameを公開した後
using var catalog = new GuaEnumCatalog();
catalog.Register("game.Phase", "First", "Second");
using var owner = runtime.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
using var phase = owner.Observe("phase", () => GuaValue.Enum("game.Phase", enemy.Phase, catalog), allowPlayer: true);
// 通常はEndWorldFrameでsample。必要ならphase.Notify()で明示通知。
```

Godotは`adapter.register_value_enum("game.Phase", ["First", "Second"])`、
`adapter.create_observe_owner(2, "enemy-1", enemy)`から`owner.observe(...)`を使う。
Ownerを保持して、treeから消えると登録を無効化、target解放とadapter破棄でも解除する。
Unityは公開済み`GuaWorldObject.Observe(...)`を使う。OnDestroyとID変更でOwnerを解除する。
Webのnative clientはPlayer固定で、detach/OnDestroyで購読を解放する。

C ABIには`gua_value_copy_enum_catalog_json`、
`gua_observe_result_copy_transport_json`を追加した。既存copy JSONは以前の形式を維持する。
C++は`enum_catalog_json()`、`observe_snapshot_transport`、
`ObserveSubscription.snapshot_transport（初期snapshot）/poll_transport()`。
.NETのlocal Contextは`GetObserveSnapshotTransportJson`と`SnapshotTransportJson/PollTransportJson`、
remote `GuaWebSocketContext`のObserve APIはtransport形式を返す。
InspectorはObserve panelで候補と通知を表示し、gap/staleで再購読を要求する。
MCP/WebMCPは同名の4 commandを公開し、remote側から登録・profile選択はできない。

## 実行した検証と残る境界

Windows MSVC / Godot 4.7 / Unity 6000.7.0b2で検証。

- native C/C++ CTest 8/8。catalog破棄後のsnapshotと履歴、空enum set、buffer契約を含む。
- .NET Selector tests 193/193（Observe transport統合9件を含む）。実native runtime→WebSocket→.NET。
- 実Godot GDScript smoke：enum、空list、Player投影、非公開化のgap、破棄通知。
- 実Unity desktop Player：Debug/Player 2/2。World位置、追加phase、候補、空list、秘密値除外、変更、reset。
- 実Godot→Inspector/MCPの各WebSocket client：独立購読、semantic click、変更poll。
- TypeScript全workspace check成功。対象Bun tests 226/226。
  WebMCPはcapability、購読解除、timeout後の遅延応答の解除を試験。

外部clientの再現はGodotで`--headless --path examples/godot-gdscript --script scripts/gua_observe_fixture.gd`
を起動し、`GUA_BRIDGE_PORT`と`GUA_OBSERVE_BRIDGE_URL`を揃え、
`bun scripts/run-observe-client-smoke.ts`を実行する。
Godot Web/Unity WebGLの実exportをheadless Chromeで起動し、実Object登録→Player snapshot→
SDK toolからsemantic click→両独立購読の変更通知→解除を確認した。
Godotは空enum set、Unityは空enum list。両方ともenumTypeと候補を保持する。
UnityのIL2CPPで非参照Bootstrap assemblyが除去される問題を再現し、
`AlwaysLinkAssembly`でフレーム駆動を維持する修正後の実exportで確認した。
.NETは応答喪失時のsocket破棄・再接続と64件上限の明示拒否後の既存token維持を試験。
不足symbolのDLL注入では初期化失敗時のnative destroyが一度だけ呼ばれることを確認した。

Webの再現はGodotのmain sceneを`GuaObserve.tscn`へ設定してWeb debug exportし、
Unityは通常fixtureをWebGLへexportする（TMP準備は既存`import-unity-tmp-resources.ps1`）。
`bun scripts/verify-observe-web-export.ts godot <export-directory>`、
`bun scripts/verify-observe-web-export.ts unity <export-directory>`を実行する。
Godot desktop/Webは同じ`gua_observe_fixture_host.gd`で登録し、Unityもdesktop/Webで同じfixtureを使う。
Web試験のmodelContext登録先はtool実行を呼び出す試験hostであり、データと操作は実エンジン。
Chrome実験版のネイティブmodelContext API、他OS、remote CIは未検証。
