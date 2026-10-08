# gua-value-tools

## JSONを受け取る前に知っておくこと

敵のphaseをenumとして比較する、整数を別の言語へ渡す、という用途の
共通データpackageです。型やenum候補を保持して、読取・検証・出力・比較します。
catalogはenum型名と許される候補の対応表です。
外部JSONは元の文字列をparseValueへ渡してください。先にJSON.parseすると
数値が丸められ、検査に必要な元の表現を失う場合があります。

[parseValue / createValue / valuesEqual](src/index.ts)が入口です。
下の例を[Value契約](../../protocol/specs/value-v1.md)と読み合わせ、
[共通準備とValue test/check](../../docs/developer-reading-guide.ja.md)で型・境界・比較を確認します。
このpackageはgameの観測・操作やtransportを実装しません。
ローカル手順は今回未実行・未検証です。

Gua共通Value v1の型・検証・等価比較。契約は
[`protocol/specs/value-v1.md`](../../protocol/specs/value-v1.md)を参照する。

```ts
import { EnumCatalog, parseValue, valuesEqual, serializeValue } from "gua-value-tools";

const catalog = new EnumCatalog();
catalog.register("game.BossPhase", ["First", "Second"]);
const value = parseValue('{"type":"enum","enumType":"game.BossPhase","value":"Second"}', catalog);
console.log(serializeValue(value, catalog));
console.log(valuesEqual(value, value, catalog));
```

外部JSONは`JSON.parse`を先に呼ばず、元の文字列を`parseValue`に渡す。
integerの小数・境界検証はbinary64へ丸める前に行う必要がある。
`createValue`はJS値を検証してコピー・freezeし、`ValueError`はcodeとpathを返す。
enum値の検証・出力・比較にはcatalogが必要。
既存World/Input/Observeのtransportを変更するパッケージではない。

検証: `bun run --filter gua-value-tools check` / `bun run --filter gua-value-tools test`。
