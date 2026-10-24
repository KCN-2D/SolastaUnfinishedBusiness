# ローカルビルド

リポジトリのルートから実行します。

```powershell
.\tools\CustomBuildLocal\BuildCustomLocalPackage.cmd
```

必要なコマンドは`.NET SDK 9`以降、`git`、`tar`、`robocopy`です。参照DLLはリポジトリの`SolastaFiles`を使います。

現在の`HEAD`から一時作業フォルダを作り、ステージ済み・未ステージ・未追跡のソース変更とローカル実行時パッチを反映します。`tools`配下はコピーしません。`Release Workflow`の出力先は一時ステージングへ明示します。

| 出力 | 場所 |
| --- | --- |
| MOD本体 | `tools\CustomBuildLocal\Output\SolastaUnfinishedBusiness` |
| UMM用ZIP | `tools\CustomBuildLocal\SolastaUnfinishedBusiness.zip` |

前提条件と出力先だけを確認するには、`-Preflight`を指定します。

```powershell
.\tools\CustomBuildLocal\BuildCustomLocalPackage.cmd -Preflight
```

## Gitの追跡範囲

`BuildCustomLocalPackage.cmd`、`internal/BuildCustomLocalPackage.ps1`、`internal/CustomBuildRuntime.patch`と、このREADME・Git設定を追跡します。ビルド成果物、ZIP、キャンペーン翻訳、ログ、レポートは除外します。
