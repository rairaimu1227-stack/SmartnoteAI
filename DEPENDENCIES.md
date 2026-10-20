# 採用依存関係

調査日: **2026-10-21 (Asia/Tokyo)**。NuGet v3 flat-containerおよびnpm registryの公開メタデータを実際に取得して確認しました。NuGetはプレリリースを除く最大の安定バージョン、npmは公開`version`を参照しました。バージョン文字列を推測して採用していません。

| 項目 | 採用版 | 調査元 |
|---|---|---|
| .NET SDK | 10.0.402 | [Microsoft SDK配布](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0/latest.version) |
| Microsoft.Data.Sqlite | 10.0.13 | [NuGet公式メタデータ](https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite/index.json) |
| Microsoft.Web.WebView2 | 1.0.4258.31 | [NuGet公式メタデータ](https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/index.json) |
| xUnit（v2系列） | 2.9.3 | [NuGet公式メタデータ](https://api.nuget.org/v3-flatcontainer/xunit/index.json) |
| xunit.runner.visualstudio | 4.0.1 | [NuGet公式メタデータ](https://api.nuget.org/v3-flatcontainer/xunit.runner.visualstudio/index.json) |
| Microsoft.NET.Test.Sdk | 18.11.0 | [NuGet公式メタデータ](https://api.nuget.org/v3-flatcontainer/microsoft.net.test.sdk/index.json) |
| Tiptap core / starter-kit / task-list / task-item / image | 3.32.0 | [npm coreメタデータ](https://registry.npmjs.org/@tiptap%2fcore/latest) |
| Monaco Editor | 0.57.0 | [npmメタデータ](https://registry.npmjs.org/monaco-editor/latest) |
| TypeScript | 7.0.2 | [npmメタデータ](https://registry.npmjs.org/typescript/latest) |
| Vite | 8.3.4 | [npmメタデータ](https://registry.npmjs.org/vite/latest) |
| Vitest | 5.0.5 | [npmメタデータ](https://registry.npmjs.org/vitest/latest) |
| Playwright Test | 1.64.1 | [npmメタデータ](https://registry.npmjs.org/@playwright%2ftest/latest) |
| Prettier | 3.9.10 | [npmメタデータ](https://registry.npmjs.org/prettier/latest) |

xUnitは既存のVSTest統合を使える安定したv2パッケージを選択しました。xUnit v3への移行はこのPoCでは行っていません。

## 固定方法

- `global.json`: SDK 10.0.402、パッチのみロールフォワード可。
- 各csproj: PackageReferenceの版を明記。
- 各 `packages.lock.json`: 間接依存とcontent hashを固定。CI/再現ビルドで`--locked-mode`を使用。
- `package.json`: `^`/`~`を使わない固定版。
- `package-lock.json`: npmの間接依存とintegrityを固定、`npm ci`で復元。
- WebView2 **SDK**は固定。**Evergreen Runtime**はホストOS上で更新されるため固定しない。実機試験時はRuntimeバージョンを記録する。
- Windows自己完結出力の実ランタイム版は `artifacts/win-x64/SmartNote.Desktop.deps.json` で確認できる。

## 開発環境

Linux x64、Node 24.19.0、npm 11.9.0、.NET SDK 10.0.402。Chromiumの実測版はTEST_RESULTSに記録する。ビルド時に依存取得のネットワークは必要だが、同梱エディタの実行時CDN依存はない。

GitHub Actionsのビルド定義も追加していますが、今回このチャットからActions実行は行っていません。ワークフロー定義が存在することとWindowsで検証済みであることは別です。
