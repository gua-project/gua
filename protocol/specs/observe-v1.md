# Observe / World Property v1

Issue #119 / G-02。構造の正本は `observe-v1.schema.json`、値は既存の
[Value v1](value-v1.md)。OPEN-03、およびG-02が担当するOPEN-04を本書で確定する。
Scenarioのselector解決や条件評価はここでは定義しない。

## 公開・登録・寿命

sourceはui/object/world。UIとObjectは、公開済みツリーのRuntime IDに結び付く
Ownerを明示作成する。worldは空Runtime IDのWorld全体Owner。各source/Runtime IDに
live Ownerは一つ。Observe名はOwner内でのみ一意で、複数敵のphaseは衝突しない。
名前・Runtime IDは有効なUTF-8、4096 bytes以下、NULなし。名前は空を拒否する。
標準fieldとカスタムObserveは別の参照領域で、同名でも上書きや同一視をしない。
worldPosition等の公開済み標準情報は再登録せず既存ツリーから取得する。

登録は最初に `unavailable / not_sampled` を公開する。初回成功はadded。
同名重複は拒否し、解除後の再登録には新しいregistrationIdを与える。
Owner破棄、成功したツリーフレームからの削除で登録も終了する。単なる非表示では
Debug登録を消さない。同IDを一度削除して再生成しても古いOwnerは復活しない。
削除フレームを挟まずゲームObjectを差し替える場合、ホストは旧Ownerを明示破棄する。
数値IDだけからその差替えを推測しない。

通常はUI/Worldそれぞれのフレーム末にgetterを評価してstagingし、対応する
フレームが成功したときだけ公開する。不正・abortフレームのsampleは公開しない。
Cホストは自分で評価し、`gua_observe_publish(..., stage=1)`をend_frame前に呼ぶ。
C++ `Context::end_frame/end_world_frame` と.NET `EndFrame/EndWorldFrame`は登録済み
getterを評価する。getterはホストスレッドで動き、C ABI callbackではない。

`Notify` / `notify` / stage=0はその場のValueを所有コピーして直ちに公開する。
同じframe内のFirst→Second→Thirdも、明示公開されたものは順序を保持する。
未通知のA→B→Aは次の評価で同じAなので変更なし。任意の内部代入は検知しない。
明示通知は同じ登録の古いstaged sampleを破棄する。collectionを毎回コピーし、
in-place変更を検出する。set順序だけの変更はValue等価なので通知しない。

成功したcontext resetはflagsにかかわらずepochを進め、全Owner・登録を失効させる。
旧購読はstale_sessionを返し、unsubscribeで解放できる。失敗したresetでは変えない。
getter実行前と結果公開時に登録の生存を確認し、途中で解除/resetした結果は捨てる。
明示通知の開始時点で失効済みならstaleを返す。フレーム評価では失効登録を静かに除去する。
wrapperの登録tokenを破棄するとgetterのフレーム登録も除去し、次のフレームを待たない。
contextを破棄すれば観測状態も終了する。

## Snapshot、Change、連続性

Snapshotは現在の登録とavailable/unavailable状態。Valueがないときvalueを省略する。
Changeのaddedはafter、changedはbefore/after、removedはbeforeを持つ。
元から取得不能ならremovedにもbefore Valueはなく、beforeStatus/beforeErrorを残す。
失敗への遷移はunavailable、失敗後の成功はrecovered。値・状態・理由が同一なら
重複通知しない。過去の正常値を失敗中の現在値として返さない。

errorはValueの安全なコード1..11、getter_failed=100、not_sampled=101、
sensitive=102。失敗理由に任意の例外本文、入力値、stack、未知field名を含めない。
null/object/入れ子collectionは禁止のままで、不正Valueを理由付きで取得不能にする。
取得不能区間をfor条件の維持成功へ読み替えてはならない。

sourceIdはcontextごとの識別子、sessionEpochはreset境界。ownerId/registrationIdは
context内で再利用しない。uiFrame/uiRevisionとworldFrame/worldRevisionは独立した
直近の確定ツリー参照。sequence/revisionは追加観測のprofile別通知順序・変更版数。
明示通知は直近の確定フレームを参照するが、ツリーとの同時取得を意味しない。
Snapshotは保持中の最新公開値であり、未sampleのゲーム内部状態の鮮度は保証しない。
同一値の再評価で通知がなくても、Snapshot取得は新しい読取時点である。
収集時刻や仮想時刻を勝手に生成せず、Traceが必要な取得時刻を別に関連付ける。

subscribeはSnapshotと開始cursorを一つのcontext lockで取得する。pollは購読者ごとに
独立しており、他購読者やaction completion queueを消費しない。結果はimmutableな
所有ハンドルで、サイズ照会とコピーの間の更新に影響されない。短いbufferには空文字を
書き、必要サイズを返す。切れたJSONを返さない。結果はcontext破棄後も読める。

履歴はprofileごとに既定1024 event / JSON UTF-8 8 MiBの両上限。正の値に設定可能。
古い履歴から削除し、一件でbyte上限を超えてもsequenceの欠落が残る。pollは保持範囲を
越えたcursorへ `gap` と空eventsを返し、再購読するまでgapを維持する。`stale_session`
も正常空状態ではない。購読し直したSnapshotは失われた中間履歴を復元しない。
この上限は通知履歴のもので、ホストが登録・現在値・購読・返却結果を無制限に作ることへの
総メモリ制限ではない。不要なOwner、登録、購読、結果を利用側が解放する。

## 公開profileと秘密値

既定はDebugのみ。Playerへの公開には登録のallow_playerと、既存のOwner投影条件の
両方が必要。World Propertyにはノードの親がないため明示allow_playerを条件とする。
標準fieldの個別field-ruleを同名Observeへ流用しない。秘密の追加値にはsensitiveを指定する。
sensitiveの値はコピー・Snapshot・通知履歴へ入れる前に除外し、取得不能理由だけを残す。
getterは通常どおり評価するが、その戻り値・例外本文を診断へ出さない。

PlayerとDebugのsequence/revision/historyは分離する。Playerで見えるOwner集合に
含むのは、allow_playerの登録を一つ以上持ち、Owner投影条件も満たすOwnerだけである。
登録なし・Debug専用登録だけのOwnerの作成・破棄ではPlayer履歴やrevisionを変更しない。
Playerで見えるOwner集合が
変わるとPlayer履歴を破棄して既存cursorをgapにする。これは削除・非表示・公開範囲変更を
含む。Playerは再購読して現在の公開集合を取得する。Debugは通常のremoved等を受け取る。
この再同期により、非公開化前の履歴を後からpollして読めない。
すでに呼出側へ返却したimmutable Snapshot/Changeの回収は保証しない。

## 公開APIと互換性

C: `gua/observe.h`。全status APIは0成功、1引数、2失効、3重複、4フレームなし、5内部失敗。
versioned登録descriptor、不透明な結果ハンドルを使う。Valueコピーのため
`gua_value_clone`を追加する。既存ABI構造体・version番号・既存wireレスポンスは変更しない。
ABI呼出とcontext破棄は他のcontext APIと同様にホスト側で同期する。

C++: `gua/observe.hpp` のObserveOwner、ObserveRegistration、ObserveSubscription。
Contextの寿命tokenを追跡し、破棄後のハンドル解放はnativeへアクセスしない。
登録はRAIIで保持する。getterは`Value`を返し、getter保持とフレーム呼出はホスト側で同期する。

.NET: GuaContext.CreateObserveOwner / SubscribeObservations / GetObserveSnapshotJson。
UI/ObjectはOwner.Observe、WorldはOwner.Property（C++はproperty）を使う。
getterは**新しい所有GuaValue**を返す。wrapperがコピー後Disposeするので、
共有して使い回すValueを返さない。登録tokenを保持し、不要時にDisposeする。新規APIは
reset/Disposeと同期し、返却JSONのnative handleはSafeHandleで解放する。

```csharp
using var owner = context.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
using var phase = owner.Observe("phase", () => GuaValue.String(enemy.Phase));
using var subscription = context.SubscribeObservations();
var initial = subscription.SnapshotJson;
// ゲーム側で中間変化を公開したい時点:
phase.Notify();
var changes = subscription.PollJson(); // statusを必ず確認する
```

## 要件・検証・後続

| 要件 | G-02の検証 | 後続 |
| --- | --- | --- |
| AT-OBS-001 | 複数Owner同名、重複拒否、解除・破棄・再生成、reset | #120 実エンジン寿命への接続 |
| AT-OBS-003 | added/changed/removed、取得不能・復旧、理由、秘密値 | #125 Trace保存 |
| AT-OBS-004 | 原子的Snapshot+cursor、並列publish、独立購読、件数/byte欠損 | #120 transport切断、#125 欠損保存 |
| VALUE-002 | 共通Value等価・不正値・collectionコピー | #120 wireでの型保持 |
| TIME-002 | frame/explicitの順序、UI/World別frame、epoch | #125 収集時計との関連付け |

共通fixtureは `protocol/fixtures/observe-v1.json`。native/C++と.NETが遷移例を実行し、
Bun/Ajvが正常・異常documentを検証する。既存Value/UI/World/reset試験を維持する。
Godot/Unity、Gua.Runtime/bridgeのObserve公開、MCP/WebMCP、Inspectorは未接続で#120。
Trace writer/reader/Viewerと製品E2Eは#125ほか。GuaのCIからPlaytestを参照しない。
検証実績は変更の引き渡し時に記載し、未実行のremote CIやengine E2Eを成功扱いしない。

### ローカル検証記録（2026-09-29）

- MSVC 19.51 + Ninja、Godot無効・WebSocket bridge有効でビルド成功。
  VS2026標準presetはローカルのinstance検出に失敗したため、VsDevCmd経由のNinja構成を使用。
- CTest 8/8、Gua.Selector.Tests 144/144（skipなし）。C++ getter内Context moveの追加回帰も成功。
- Gua.Coreのnet10.0 / netstandard2.1ビルド、全workspaceの`bun run check`成功。
- Value/ObserveのBun 87/87、実native/.NET出力8 documentのAjv検証成功。
- 読み取り専用監査のOwner明示破棄後Player履歴、.NET getter内Disposeを失敗試験で再現し修正。
  C++ getter内Context moveも寿命tokenを維持する回帰試験で修正を確認。
- remote CI、他OS、エンジンのObserve接続、Traceへの保存は未実行・今回の統合完了対象外。
