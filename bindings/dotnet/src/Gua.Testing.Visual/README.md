# Gua.Testing.Visual

## 見た目の回帰を確認する

文字が切れる、controlがずれる、意図しないoverlayが重なる、といった問題を
PNGのbaselineと比較します。baselineは承認した期待画像で、OS/rendererの差を
`BaselineVariant`という別の組合せ名で管理します。

「許可されたhostからPNG取得 → 指定variantのbaseline読取 → mask/thresholdを適用して比較 →
actual/diff/manifest保存」の順です。baselineがない場合も通常は失敗にします。
maskは比較から除外する領域で、秘密を自動検出する機能ではありません。
画像比較の成功は操作の完了やゲーム状態の正しさを代わりに証明しません。

[GuaVisualAssertions.ExpectScreenshotAsync](GuaScreenshotComparison.cs)と
[VisualTests](../../tests/Gua.Visual.Tests/VisualTests.cs)で、mask・寸法違い・差分成果物を追えます。
下のoptions例を読み、[共通の確認準備とsuite別手順](../../../../docs/developer-reading-guide.ja.md)のVisual suiteで比較処理を確認します。
実engineの描画確認は、そのengineと同じrendererの別の確認です。今回ローカルでは未実行です。

`Gua.Testing.Visual` adds opt-in PNG baseline comparison to ordinary .NET tests.
The package targets both `net10.0` and `netstandard2.1`.
Semantic assertions should remain the primary test path; visual comparison covers
rendering regressions such as clipping, misplaced controls, incorrect assets, and
unexpected overlays. PNG codec dependencies stay out of `Gua.Testing` and
`Gua.Testing.Godot` unless a visual test explicitly references this package.

## Basic usage

Capture or update a baseline deliberately, then compare later runs:

```csharp
using Gua.Testing.Visual;

var result = await GuaVisualAssertions.ExpectScreenshotAsync(host.Context, "title-screen", new()
{
    BaselineDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "baselines"),
    ArtifactDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "gua"),
    BaselineVariant = Environment.GetEnvironmentVariable("GUA_VISUAL_VARIANT")
        ?? "local-windows-godot-gl-compatibility",
    PixelThreshold = 0.02f,
    MaxDifferentPixelRatio = 0.001,
    WaitForStableSnapshot = true,
});
```

Missing baselines fail unless `UpdateBaselines = true` or
`GUA_UPDATE_BASELINES=1` is explicit. A successful comparison writes its current
`actual.png` and a `comparison.json` manifest with reason `matched`, allowing a
visual report to show the screen even when no difference was found. A pixel-difference failure writes
`expected.png`, `actual.png`, `diff.png`, and `comparison.json`. Missing-baseline
and dimension-mismatch failures write the same manifest with only the PNGs that
exist for that failure. The manifest records the comparison name, variant,
dimensions, reason, metrics, thresholds, masks, and diagnostic baseline path.
Pass the failure artifact path returned by `GuaDiagnosticWriter` as
`FailureDirectory` when visual artifacts should share the same diagnostic
directory.

Use masks only for genuinely nondeterministic regions such as a clock or generated
avatar. Masks are excluded from both the diff and the compared-pixel denominator;
dimension mismatches never resize implicitly.

## Baselines in CI

Commit reviewed baseline PNGs to the test repository. Normal pull-request and main
branch jobs must run without `GUA_UPDATE_BASELINES`; a missing or changed baseline
must fail the job and upload the Gua artifact directory for review. Keep baseline
updates in an explicit developer command or manually dispatched workflow, review
the PNG diff, and commit the accepted files. Do not let an ordinary CI job update
and accept its own baselines.

Example GitHub Actions steps:

```yaml
- name: Run rendered visual tests
  shell: pwsh
  env:
    GUA_VISUAL_VARIANT: windows-godot-gl-compatibility
  run: dotnet test tests/Game.Visual.Tests/Game.Visual.Tests.csproj --configuration Release

- name: Upload visual failures
  if: failure()
  uses: actions/upload-artifact@v4
  with:
    name: gua-visual-failures
    path: artifacts/gua
    if-no-files-found: ignore
```

To render these data artifacts as a static report or publish the latest `main`
result through GitHub Pages, use the separate
`link1345/gua-tester/visual-report` action. It combines the manifests and PNGs
with a prebuilt Astro viewer without adding JavaScript dependencies to this
package or installing Astro in the consumer test job.

Screenshots can contain rendered secrets or personal information. Review the
captured content before enabling Pages, especially for public repositories.

## OS and renderer variants

Gua never infers variants from the current machine. Choose a stable, explicit name
that describes every rendering input allowed to produce a different baseline, for
example:

- `windows-godot-gl-compatibility`
- `windows-godot-forward-plus`
- `linux-godot-gl-compatibility`

Set the variant from the CI matrix or a repository-owned helper and pass it through
`BaselineVariant`. Keep resolution, DPI scaling, font files, locale, theme, engine
version, renderer, and GPU/driver policy deterministic. Do not create a new variant
for arbitrary transient differences; that hides regressions instead of explaining
them.

Operation recording and replay live in the separate `Gua.Testing.Recording`
package and do not pull PNG dependencies into non-visual test projects.
