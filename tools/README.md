# 開発・検証ツール

用途に合う入口を選び、リンク先の手順をリポジトリのルートから実行します。

| 場所 | 用途 |
| --- | --- |
| [tools/CustomBuildLocal/](CustomBuildLocal/README.md) | 正式ビルドとパッケージ生成 |
| [tools/asset-authoring/](asset-authoring/README.md) | Blenderによるモデル・骨格アニメーションの生成、書き出し、再読み込み検証、プレビュー |
| [tools/blender-models/](blender-models/) | モデル別の生成ソース・制作レシピと検証手順 |
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

ゲームと制作ツールを終了して、`Clear-ToolStorage.ps1`で生成物を整理します。引数なしで対象を確認し、`-Apply`で削除します。資源の制作記録や保持先が変わった場合は、必須検証でも自動実行します。

モデルのbundleと、制作記録が宣言する材質・テクスチャ等の中間データを整理します。採用中と直前のモデル、その再制作に必要な原本を保持します。継続中の作業は`-PreserveDirectory`で指定します。完了・失敗・中断した候補は作成日時にかかわらず整理対象です。採用の判定には、実機確認の記録と一致するcatalogのSHA256を使います。

```powershell
.\tools\Clear-ToolStorage.ps1
.\tools\Clear-ToolStorage.ps1 -Apply
```

公開済みZIPは公開記録のSHA256と照合して整理します。旧候補の配布ZIPも、新しいモデルの採用後に整理します。manifest、レシピ、制作・検証記録は保持します。

実ゲーム証跡の保持予算は既定40GBです。`-MaxTestAgeDays`で保持日数、`-PreserveDirectory`で保持する作業フォルダーを指定できます。整理結果とドライブ空き容量の変化を`tools/game-test/artifacts/ToolStorage/`に保存します。`tools`の容量は、論理サイズ、ハードリンクを重複計上しないサイズ、NTFS圧縮とスパースファイルを反映した保存量を記録します。フォルダー別の専有量には、ハードリンクがそのフォルダー内で完結するファイルを計上します。
