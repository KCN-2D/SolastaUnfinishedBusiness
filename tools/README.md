# 開発・検証ツール

リポジトリのルートから実行します。

| 場所 | 用途 |
| --- | --- |
| [CustomBuildLocal](CustomBuildLocal/README.md) | 正式ビルドとパッケージ生成 |
| [asset-authoring](asset-authoring/README.md) | Blenderによるモデル・骨格アニメーションの生成、書き出し、再読み込み検証、プレビュー |
| [blender-models](blender-models/) | モデルごとの生成処理と検証手順 |
| [game-test](game-test/README.md) | 環境診断、隔離ビルド、明示配置、実ゲーム検証、証跡保存、復元 |
| [runtime-verification](runtime-verification/README.md) | ゲーム内プローブのコンパイル・実行と後始末を担う検証基盤 |
| [localization](localization/) | 翻訳ファイルの整備と検査 |

モデルを変更する場合は、生成・再読み込み検証・プレビュー確認の後に、`tools\CustomBuildLocal\BuildCustomLocalPackage.cmd`でビルドします。実ゲームの確認には`game-test`のケースを使います。

`PASS`は実行した工程・ケースの範囲に属します。`game-test`の非占有native runnerは、OS入力の非占有、通常アクション、描画、復元を確認します。Unityの実フォーカス喪失、通常UI入力、可聴音はそれぞれ別に検証します。`asset-authoring`のBlender工程内検証、Unity資産のビルド・再読込、ゲーム内での資産統合も分けて記録します。

`CustomBuildLocal`のビルド本体、`game-test`の実行スクリプトとプローブ、依存する`runtime-verification`の共通基盤はGitの追跡対象です。ビルド成果物、ケースごとのセーブ・個人設定、ログ、レポート、一時ファイルは除外します。`blender-models`と検証基盤の個別プローブはローカル専用です。

ビルド成果物は`CustomBuildLocal/Output`、3D制作の作業データは`asset-authoring/workspace`、実ゲーム検証の結果は`game-test/artifacts`に保存します。
