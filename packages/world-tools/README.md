# gua-world-tools

## game世界の公開情報を読む

例えば「playerから12単位以内のenemyを探す」ための型・検証・読取toolを共有します。
providerはengine側が用意するデータ取得先で、公開対象objectを明示登録してから利用します。
UI操作やgame inputとは別の読取機能であり、任意のscene走査・world操作は提供しません。

[parseWorldObjectTree / selectorFromArguments / registerWorldWebMcpTools](src/index.ts)から
応答検証とbrowser tool登録を追えます。近傍検索は一つのPlayer公開snapshotで評価します。
epochはreset世代、frameは公開回、revisionは内容の変更を識別し、distanceの単位はhostのworld単位です。
非公開・不明・座標を公開しない基準objectの空結果を、周囲に何もない証拠にしません。

下の使用例と[共通準備とWorld test/check](../../docs/developer-reading-guide.ja.md)で
型・selector・tool処理を確認します。spatial tool schemaの共有も、
browserへTesting/Debugの物理読取権限を与えるものではありません。

Browser-safe World Object Tree v1 types, payload validators, MCP tool definitions,
and provider adapters shared by `gui-mcp` and browser WebMCP integrations.

Browser integrations inject a `GuaWorldProvider` and call
`registerWorldWebMcpTools(document.modelContext, provider)`. The adapter always
requests the `player` projection from the provider. Engines must explicitly opt
objects into their world-frame pump; arbitrary scene objects are never exposed.

The package only defines read-only `get_world_object_tree`,
`find_world_objects`, and `wait_for_world_object` tools. It has no Node or Bun
runtime dependency and intentionally provides no world action API.

```ts
import { parseWorldObjectTree, selectorFromArguments } from "gua-world-tools";

const tree = parseWorldObjectTree(await engine.getWorldObjectTree());
const selector = selectorFromArguments({
  kind: "enemy",
  relativeToObjectId: "player",
  maxDistance: 12,
  limit: 5,
});
```

Nearby queries use the provider's world units and one Player-projected snapshot.
Results include `sessionEpoch`, `frameSequence`, `revision`, and aligned spatial
distances ordered by distance and then object ID compared as UTF-8 bytes. Private,
unknown, or coordinate-omitted reference objects all produce
the same valid empty result.
