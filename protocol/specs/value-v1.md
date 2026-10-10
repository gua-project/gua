# 共通Value v1

## 値を言語間で同じ意味にするために

敵の状態が「第2段階」へ変わったことをC#とTypeScriptで比較したいとき、
文字列や数値を適当に変換すると、型やenumの定義が失われます。
Valueは型を付けた値の共通表現です。`integer`と`number`は別型、
`list`は順序付き、`set`は順序を無視して比較する集合です。
enumのcatalogは、`game.BossPhase`のような型名と許される候補名を対応付けます。

流れは「型とenum候補を用意 → 値を生成/元JSONから読取 → 検証 → 型を保った比較/出力」です。
外部JSONは数値へ丸める前の文字列から読みます。Valueそのものはゲームを観測せず、
UIやWorldの既存フィールドを自動的にこの形式へ置き換えません。

[GuaValue.FromJson / ValueEquals](../../bindings/dotnet/src/Gua.Core/GuaValue.cs)はnativeへ委譲する入口、
[parseValue / valuesEqual](../../packages/value/src/index.ts)はTSの入口です。
[ValueTests](../../bindings/dotnet/tests/Gua.Selector.Tests/ValueTests.cs)で往復・拒否・比較を読み、
[開発者ガイドのローカル確認手順](../../docs/developer-reading-guide.ja.md)のSelector/Value suiteで確認します。
以下のschema・ABI条件が規範で、末尾の成功件数は過去の検証記録です。

Issue #118 / G-01。`value-v1.schema.json` と `enum-catalog-v1.schema.json` が
追加観測Valueの構造の正本であり、本書が意味検証・比較・ABIを規定する。
既存World state、UI value、Input vector2とは別の契約である。

## wireと比較

```json
{"type":"integer","value":42}
{"type":"number","value":42}
{"type":"enum","enumType":"game.BossPhase","value":"Second"}
{"type":"list","elementType":"enum","enumType":"game.BossPhase","value":[]}
{"type":"set","elementType":"string","value":["key","map"]}
```

型はbool / integer / number / string / enum / list / set。
必須・許可フィールドはschemaの各分岐に限定し、未知・不要フィールドを拒否する。
null、任意object、object参照、入れ子collection、BigIntは禁止する。
collection要素は裸の同種scalarで、空でもelementTypeと必要なenumTypeを保持する。

- integer: ±9007199254740991の整数。JSONの小数点・指数表記でも数学的に整数なら許可。
  `1.0`と`1e0`はintegerとして有効だが`1.00000000000000001`は拒否する。
  JSON読取でbinary64へ丸める前に検証する。JSの数値入口では既に失われた精度を
  復元できないため、外部wireは必ず`parseValue`へ元のJSON文字列で渡す。
- number: 有限binary64。JSON decimalをbinary64に丸める。overflowは拒否、
  underflowはbinary64に従い0へ丸める。NaN/Infinityは禁止。
- numberとintegerは別型。型の暗黙変換、文字列から数値への変換は行わない。
- 文字列は有効なUnicode scalar列。大小文字変換・Unicode正規化を行わない。
  `é`と`e + combining acute`は非等価。不正UTF-8・孤立サロゲートは拒否する。
  NULを含む文字列は許可し、UTF-8の長さ付きC ABIで保持する。
- 型、collection種別、elementType、enumTypeが異なる有効値は非等価。
  不正値は非等価に置き換えず、検証エラーとする。
- listは順序あり、重複可。setは順序を無視して比較し、重複を拒否する。
  setの出力順は入力順を保持する。空setにも型がある。
- 同じ数値型の-0と0は等価。両方を含むsetは重複。出力は0に統一する。
  JSONのバイト一致やハッシュの標準化は規定せず、公開の等価APIを使う。

JSONは深さ64まで、重複objectキーと不正な構文を拒否する。
エラーはコードとschema上のパスだけを含み、入力値・未知のキー名を含めない。
複数不正を含む入力のエラー優先順位は規定しない。

## enumカタログ

```json
{"schemaVersion":1,"enums":[{"enumType":"game.BossPhase","members":["First","Second","AliasSecond"]}]}
```

enumTypeは`.`区切りの2要素以上のASCII識別子
`[A-Za-z_][A-Za-z_0-9]*`とする。候補名は空でないUnicode文字列で重複禁止。
候補集合は空にできない。カタログは空でもよい。

カタログ内で同じID・同じ候補集合の再登録は順序にかかわらず成功する。
最初の候補順を保持し、異なる集合の再登録は原子的に拒否する。
追加・削除・改名には`game.BossPhaseV2`等の新IDを使う。グローバルな自動登録や
言語の型名からの自動ID推測は行わない。独立カタログ間の衝突解決は呼出側の責務である。

候補名とenumTypeを正としnumeric valueは送らない。aliasも別名なので非等価。
flagsの数値合成や`A|B`の自動分解は行わず、明示された候補名だけ受理する。
scalarだけでなく空enum collectionにも登録済みenumTypeが必要。
カタログJSON出力から候補一覧を取得でき、C#とTSには候補取得メソッドもある。

## 公開API・所有権

- C: `gua/value.h`。不透明なValue／Catalogハンドル、versioned descriptor、
  型付き生成、JSON読取・出力、型情報、登録、比較、破棄を提供する。
  `gua_enum_catalog_validate_type`で候補取得前にもUnicode・ID形式・登録有無を検証する。
  `gua_value_text_t.size`はUTF-8バイト数。入力をコピーし、collectionも要素を所有する。
  関係する元ValueやCatalogの破棄後も生成済みValueは有効。
- C++: `gua/value.hpp`の`gua::Value`／`gua::EnumCatalog`。
  move可能なRAIIラッパーと型別factoryを提供する。C ABIへ委譲し、別の比較実装を作らない。
- C#: `Gua.Core.GuaValue`／`GuaEnumCatalog`。SafeHandle／P/Invokeで同じnative実装を使用。
  `Bool`、`Integer`、`Number`、`String`、`Enum`、`Collection`、`FromJson`、`ToJson`、
  `ValueEquals`を提供する。Disposeとの競合ではSafeHandle参照でnative寿命を保持する。
- TS: `gua-value-tools`。判別union、`createValue`、`parseValue`、`serializeValue`、
  `valuesEqual`、`EnumCatalog`を提供する。検証時にコピー・freezeする。
  enum値の検証・出力・比較には対応するcatalogを渡す。

Cのstatusは0が成功。失敗時のoutハンドルはNULL。C++／C#／TSは型付き例外へ変換する。
生成済みValueは不変で並行読取可。Catalog登録・破棄は読取と呼出側で同期する。
C出力は終端NULを含む必要バイト数を返す。NULL/0でサイズ取得し、短いbufferは空文字にする。
0の戻り値は不正なbuffer指定などの失敗。切れたJSONは返さない。
C ABI境界のC++例外は捕捉し、allocation等の失敗はinternalに変換する。

| Cコード | TSコード | 意味 |
| --- | --- | --- |
| 1 | structure | JSON構造、必須・未知フィールド、API引数の不正 |
| 2 | forbidden_type | 禁止型、入れ子、未知のtype |
| 3 | range | integer範囲外または非整数 |
| 4 | non_finite | 非有限numberまたはoverflow |
| 5 | unicode | 不正UTF-8または孤立サロゲート |
| 6 | enum_unknown | 未登録enumType |
| 7 | enum_conflict | 同じIDで異なる候補集合 |
| 8 | enum_member | 候補にない名前 |
| 9 | element_type | scalar値／collection要素の型不一致 |
| 10 | duplicate | set要素／enum候補の重複 |
| 11 | internal | メモリ割当等の内部失敗 |

## 互換性と後続統合（OPEN-01 / OPEN-02）

G-01のOPEN-01は独立Value v1 schemaと追加のC ABIシンボルで解決する。
既存ABI構造体・wire schema・protocolSchemaVersion・abiVersionは変更しない。
旧clientがadditionalPropertiesを拒否する既存レスポンスへ新フィールドを追加しない。
型生成器は導入せず、手書きの各言語型を共通fixtureで照合する。

G-01のOPEN-02は上記enum・Unicode・integer/number・collection規則で解決する。
containsAll/Any、regex等の条件演算子はP-03で定義する。Observe/Property、
Snapshot/Change、Assertion、注釈への組み込み時にはこのValue契約を参照する。
それらのエンドポイント・新field・capability negotiationはG-02/G-03以降で実装し、
本変更だけで旧clientとの新Observe通信が完成したとは扱わない。

## 要件と検証の対応

共通fixtureは `protocol/fixtures/value-v1.json`。`valid`と`invalid`は元JSON文字列を保持し、
丸め前の整数や重複キーも照合する。`comparisons`は型を含む等価期待値、
`generators`はNaN/Infinity/BigIntの生成指示である。BigIntを型として持たないC/C#では
対応factoryが存在しないことと未知タグ拒否を確認する。

JSON Schemaは構造・型・通常の数値範囲・uniqueItemsを検証する。
enum参照／定義衝突、Unicode、重複JSONキー、丸め前の整数、非JSON値は意味検証で保証する。
`schemaReject`は汎用JS schema validatorでも拒否できる異常例を区別する。

| 要件 | 検証 | ローカル結果 |
| --- | --- | --- |
| AT-VALUE-001 | 全7型、各空collection、往復、型保持 | native / C# / TS 成功 |
| AT-VALUE-002 | 整数境界・丸め、小数、非有限、禁止型、Unicode、識別可能な拒否 | native / C# / TS 成功 |
| AT-VALUE-003 | enum候補取得、型違い、alias、定義衝突 | native / C# / TS 成功 |
| AT-VALUE-004 | set順序、list順序、重複、-0、空の型 | native / C# / TS 成功 |
| AT-VALUE-005 | 既存World nullとInput vector2、新Value制限の分離 | 互換回帰試験で確認 |
| ASSERT-003 | integer/number、enumType、collection型を含む等価 | native / C# / TS 成功 |

MSVC CTest、NUnit、Bun/Ajvで同じfixtureを消費する。CIにはportable native jobの
CTest／NUnitと両managed targetビルド、既存TS jobのValue試験を組み込む。
ローカルWindowsの成功と、未実行のリモートCI・他OS・後続Observe統合は区別する。
