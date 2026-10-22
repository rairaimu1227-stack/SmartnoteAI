# SmartNote AI — PoC v0.2.0

Windows向けのローカルメモアプリです。WPFの常駐シェルに、同梱WebView2 + Tiptap + Monacoを組み合わせています。AI/API通信・Google同期は実装していません。

**実装・クロスビルドと自動試験を実施済みです。Windows実機でのWPF/WebView2/通知領域/ホットキー操作は未検証であり、PoCの最終合格は未判定です。** 詳細は [TEST_RESULTS.md](TEST_RESULTS.md) を参照してください。

## 動作環境

- Windows 10/11 x64（実機試験はWindows 11を推奨）
- Microsoft Edge WebView2 Evergreen Runtime
- 配布版は.NETランタイムを同梱。Webエディタも同梱し、実行時にCDNへアクセスしません。
- 開発時: .NET SDK **10.0.402**、Node.js **24.x**、npm **11.x**。Visual StudioはこのSDKとWPFをサポートする版と「.NETデスクトップ開発」ワークロードが必要です。

## 配布版の起動

GitHubから取得する場合: [PoC v0.2.0 配布ZIP](https://github.com/rairaimu1227-stack/SmartnoteAI/raw/refs/heads/poc-v0.2.0-download/artifacts/SmartNoteAI-PoC-v0.2.0-win-x64.zip)（約79MB）。[配布ブランチとSHA-256](https://github.com/rairaimu1227-stack/SmartnoteAI/tree/poc-v0.2.0-download/artifacts)も参照できます。Windows実機での動作確認は未実施です。

`artifacts/SmartNoteAI-PoC-v0.2.0-win-x64.zip` を展開し、フォルダ内の `SmartNote.Desktop.exe` を実行します。exe単体ではなくフォルダ全体が必要です。

初回を含めて通常起動は**通知領域へ常駐し、メイン画面を表示しません**。アイコンをダブルクリックするか、メニューの「メイン画面を表示」を選んでください。すぐ表示する場合:

```powershell
.\SmartNote.Desktop.exe --show
```

×は非表示、通知領域の「完全終了」は保存確認後の終了です。再実行は既存プロセスへ表示要求を送ります。初期設定ではCtrl+Alt+Mでクイックメモを開きます。設定画面の「クイックメモのショートカット」で変更できます。ホットキー競合時は通知とステータスに原因を表示し、通知領域メニューから開けます。

## ビルドとテスト

リポジトリのルートで実行します。

```powershell
./scripts/build.ps1
```

このスクリプトはnpmの固定依存復元、Webビルド、Vitest、Playwright、NuGetの固定依存復元、ソリューションビルド、xUnit、win-x64への自己完結発行、ZIP作成を行います。ブラウザを取得できない環境では `-SkipBrowserTests` を指定できますが、試験省略を記録してください。

個別に実行する場合:

```powershell
cd editor-web
npm ci
npm run build
npm test
npx playwright install chromium
npm run test:e2e
cd ..
dotnet restore SmartNote.sln --locked-mode
dotnet build SmartNote.sln -c Release --no-restore
dotnet test SmartNote.Tests -c Release --no-build
dotnet publish SmartNote.Desktop -c Release -r win-x64 --self-contained true --no-restore -o artifacts/win-x64
```

Visual Studioで `SmartNote.sln` を開く前にも `npm ci` / `npm run build` が必要です。Webビルドがない場合、Desktopプロジェクトは明示的にビルドエラーを出します。起動プロジェクトは `SmartNote.Desktop` です。

Linux上でも.NET SDKとNodeを用意すればクロスビルドできます。既存Chromiumを使用する場合は `CHROMIUM_PATH=/usr/bin/chromium npm run test:e2e` とします。Linux上でWPFを実行することはできません。

## 基本操作

- メモ →「＋ 新しいメモ」。タイトル・本文は約1秒後に自動保存され、Ctrl+Sでも保存できます。
- 太字・斜体・見出し・箇条書き・チェックリスト・コードブロックをツールバーで編集できます。
- チェック項目内にカーソルを置き「項目 ↑ / ↓」で同じリスト内を移動します。BlockIdは変わりません。
- PNG/JPGは画像ボタンまたはクリップボードから挿入します。元ファイルは変更せず、管理領域へコピーします。上限10MBです。
- タブを閉じる前に保存します。失敗した場合は閉じません。ホームやタスクへの移動でもタブは保持します。
- ノートのチェック項目は全件タスク登録されます。本文・チェック状態・並び順が変わってもIDで関連付けます。チェック項目削除で派生タスクを削除しますが旧ノートは履歴に残ります。
- タスク画面での完了変更はノートにも反映します。先に開いている編集内容を保存し、その後DBトランザクションで更新します。
- 設定の自動起動は初期OFFです。有効化するとユーザーのRunキーに現在のexeを登録します。配布先へ移動した場合はOFF/ONして再登録してください。
- メイン最前面は初期OFF、クイックメモは初期ONで、独立して保存します。
- クイックメモは通常一覧の「一時メモ」付きメモとして保存します。「保存して次のメモ」で別メモを開始します。

## クイックメモのショートカット設定（v0.1.1以降）

1. メイン画面で「設定」を開く。
2. 「クイックメモのショートカット」でCtrl・Alt・Shiftとキーを選ぶ（例: Ctrl+Shift+Q）。
3. 「ショートカットを保存」を押す。アプリ再起動なしで適用され、再起動後も保持される。

CtrlまたはAltが必要です。英字・数字・F1～F24に対応し、F12とAlt+F4は除外します。他のアプリで使用中の組み合わせは拒否して案内します。新しいキーの登録や保存に失敗した場合、以前のキーと保存済み設定は維持します。

「ショートカットを使用する」をOFFにして保存すると無効になります。通知領域のメニューからは引き続き開けます。「初期値に戻す」→保存でCtrl+Alt+Mに戻せます。旧版の設定ファイルはそのまま使えます。

更新時は旧版を通知領域から完全終了してから、新しいZIPを別フォルダへ展開して起動してください。ユーザーデータは共通のLocalApplicationDataを使います。自動起動を有効にしている場合はOFF/ONして新しいexeのパスを登録し直してください。

## v0.2.0 レビュー反映

- ノートとチェックを組み合わせた専用アイコンをexe・ウィンドウ・通知領域へ適用。
- ホームの集計カード、最近のメモ、メモ検索、ライト/ダークの配色・余白・編集ツールを刷新。
- 本文を選択して「文字色」「ハイライト」で変更。「標準」「なし」で解除。
- 「＋ テキスト」で独立したテキストメモを作成。Plain text / Markdown / PowerShell / JSON / XMLを選択でき、行番号・検索/置換・構文色分けを利用可能。ファイルを直接編集する機能ではありません。
- ノートの「その他の編集」→「テキストへコピー」は別メモを作ります。元ノート・画像・BlockIdは変更しません。コピー先の画像は説明文になり、装飾やタスク連動は引き継ぎません。
- 「コード」はコマンド、SQL、設定例などを改行・等幅で残す書式です。コードを実行する機能ではありません。
- タスクに優先度（指定なし/低/中/高）、期限、すべて/未完了/今日/期限超過/完了の絞り込み、優先度順/期限順を追加。
- クイックメモは電話中や別アプリ操作中の思いつきを記録する補助機能です。設定からサイドバー表示を隠せます。ショートカットの無効化とは独立しています。

更新時にDBをv2へ移行し、`smartnote.db.before-v2-*.bak` に更新前のDBを保存します。旧アプリはv2のDBを開きません。旧版へ戻す必要がある場合は、完全終了して現在のデータ一式を別途保護し、更新前バックアップを復元してください（更新後の変更は旧版へ自動移行されません）。画像フォルダも保管してください。

## 元データを保護するテキストモード（詳細機能）

ノートの「その他の編集」→「保護テキスト」から開きます。ノートの正本はTiptap形式のJSONです。Monacoには**通常のMarkdownではなく、保護された編集用表現**を表示します。

```text
# 0 doc
# 0.0 paragraph
# 0.0.0 text
text:0.0.0 "本文。改行は\nと入力"
```

`text:`の引用符内、`check:<BlockId> [x]` / `[ ]`を編集できます。検索はCtrl+F、置換はCtrl+H、Undo/RedoはCtrl+Z / Ctrl+Yです。専用構文ハイライトと行番号を表示します。

識別子・構造行・画像行の変更、行の追加・削除は保存とモード切替時に拒否します。拒否した入力はMonacoに残り、正本・DBは上書きしません。Undoで修正できます。段落やリスト、装飾の追加・削除はノートモードで行ってください。空段落への最初の本文入力はテキストモードでも可能です。

画像・未知の属性・複雑な構造は元JSONを直接保持します。Tiptapが正本を再現できない場合はノート側を読み取り専用にし、その状態を明示します。モード切替・タブ切替を越えるTiptapのUndo履歴は保証しません。

## 保存先と復旧

```text
%LOCALAPPDATA%\SmartNoteAI\
  smartnote.db          SQLite（WAL）
  smartnote.db-wal      実行中の変更（存在する場合）
  smartnote.db-shm      実行中の共有メモリ（存在する場合）
  attachments\         GUID.img（PNG/JPGの元バイト列）
  backups\             起動・正常終了時のSQLiteオンラインバックアップ
  WebView2\            WebView2のユーザーデータ
```

ユーザーデータはインストールフォルダに保存しません。保存失敗はUIに表示し、未保存内容をメモリに保持します。失敗中の終了・タブ閉鎖・画面移動を止めます。×で非表示になってもプロセスは残ります。強制終了やOS停止、WebView2クラッシュ直前の未保存分は保証できません。

破損時はアプリを停止し、上記フォルダ全体を別の場所へコピーして保護してください。**元ファイルを消さず**、DB/WAL/SHMを別名へ退避した後、正常な `backups/*.db` を `smartnote.db` として配置します。画像フォルダは維持します。起動後にノートと画像を確認してください。DBの`history`表にも上書き前の本文とタイトルがありますが、履歴復元UIは未実装です。

バックアップと履歴はPoCでは自動削除しません。バックアップを持ち出す場合は画像フォルダも一緒に保管してください。WebView2異常時は自動リロードせず、失われる可能性のある未保存内容を明示したうえで利用者が復旧を選択します。

## 設計・検証資料

- [ARCHITECTURE.md](ARCHITECTURE.md) — モジュール、保存と通信の設計
- [DEPENDENCIES.md](DEPENDENCIES.md) — 採用バージョン、調査元、固定方法
- [TECHNICAL_FINDINGS.md](TECHNICAL_FINDINGS.md) — 実測結果と代替方式
- [TEST_RESULTS.md](TEST_RESULTS.md) — 自動試験結果、POC-001〜020の実機手順
- [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) — 制約・未検証事項
- [NEXT_PHASE.md](NEXT_PHASE.md) — 本開発への移行条件
