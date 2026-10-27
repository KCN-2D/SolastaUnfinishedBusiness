# 開発・検証ツール

用途に合う入口を選び、リンク先の手順をリポジトリのルートから実行します。

| 場所 | 用途 |
| --- | --- |
| [tools/CustomBuildLocal/](CustomBuildLocal/README.md) | 正式ビルドとパッケージ生成 |
| [tools/asset-authoring/](asset-authoring/README.md) | Blenderによるモデル・骨格アニメーションの生成、書き出し、再読み込み検証、プレビュー |
| [tools/blender-models/](blender-models/) | モデルごとの生成処理と検証手順。ローカル専用 |
| [tools/game-test/](game-test/README.md) | 固定ケースの作成、検証用ビルド、明示配置、実ゲーム回帰、結果の判定、復元と証跡の容量管理 |
| [tools/runtime-verification/](runtime-verification/README.md) | 個別プローブと通常起動の確認、共通harness・デスクトップ・後始末の仕様、成果物の同期 |
| [tools/localization/](localization/) | 翻訳ファイルの整備と検査 |

モデル変更は資産制作の手順から正式ビルド、実ゲーム回帰へ進みます。各工程の合格範囲は[資産制作](asset-authoring/README.md)と[実ゲーム回帰の結果と復元](game-test/README.md#結果と復元)に従います。

## パス表記

本文の相対パスは、特記がなければリポジトリのルートを基準に `/` で表記します。Markdownリンクは文書の場所を基準にします。インストール済みゲームや実行証跡フォルダを基準にする場合は、そのことを明記します。

Windowsで実行するコマンドと絶対パスは `\` を使います。URLや識別子の表記は変えません。

## 生成物と追跡範囲

開発環境の共通実装・契約テストとREADMEをGitで管理します。個別シナリオ、テストケース、生成物の配置と追跡範囲は各ツールのREADMEに従います。

| 生成物 | 保存先 |
| --- | --- |
| 正式ビルド | [ビルド出力の一覧](CustomBuildLocal/README.md) |
| 3D制作の作業データ | `tools/asset-authoring/workspace/` |
| 実ゲーム検証のビルド・入力の複製・ログ・画像 | `tools/game-test/artifacts/` |

各ツールの終了処理で復元と一時生成物の撤去を行います。実ゲーム検証の証跡の保持と整理は、[証跡の容量管理](game-test/README.md#証跡の容量管理)に従います。
