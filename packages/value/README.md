# gua-value-tools

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
