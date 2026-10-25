# ローカルビルド

正式ビルドとUMM用パッケージの生成手順です。リポジトリのルートから実行します。

```powershell
.\tools\CustomBuildLocal\BuildCustomLocalPackage.cmd
```

必要なコマンドは`.NET SDK 9`以降、`git`、`tar`、`robocopy`です。対象フレームワークは`net481`、参照DLLはリポジトリの`SolastaFiles/`を使います。スクリプトが一時的な参照先を`SolastaInstallDir`へ設定するため、通常のビルドに環境変数の指定は不要です。

現在の`HEAD`から一時worktreeを作り、ステージ済み・未ステージ・未追跡のソース変更を反映します。ビルド専用のbranchやrelease用のrefは使いません。`tools/`配下はコピーせず、`tools/CustomBuildLocal/internal/CustomBuildRuntime.patch`を一時worktreeにだけ適用します。このローカル実行時パッチを製品ソースへ混ぜません。

`Release Workflow`の出力先は一時ステージングへ明示します。`Release Workflow`、`Release Install`、`Debug Install`は内部の構成名であり、通常の確認には上記のコマンドを使います。

| 出力 | 場所 |
| --- | --- |
| MOD本体 | `tools/CustomBuildLocal/Output/SolastaUnfinishedBusiness/` |
| UMM用ZIP | `tools/CustomBuildLocal/SolastaUnfinishedBusiness.zip` |

前提条件と出力先だけを確認するには、`-Preflight`を指定します。

```powershell
.\tools\CustomBuildLocal\BuildCustomLocalPackage.cmd -Preflight
```

## Gitの追跡範囲

`tools/CustomBuildLocal/`内のビルド入口、`internal/BuildCustomLocalPackage.ps1`、`internal/CustomBuildRuntime.patch`、このREADMEとGit設定を追跡します。ビルド成果物、ZIP、リリース文面、キャンペーン翻訳、ログ、レポート、個人用補助ファイルは除外します。

ゲームへ配置して確認する場合は[実ゲーム回帰テスト](../game-test/README.md)、個別プローブや同期・起動確認には[Runtime verification](../runtime-verification/README.md)を使います。
