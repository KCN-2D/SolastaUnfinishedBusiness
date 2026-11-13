# 翻訳作業ツール

開発用Pythonは3.14.10を使用します。環境の作成は[資産制作ツールのPython設定](../asset-authoring/README.md)を参照してください。翻訳用パッケージのバージョンは[requirements.txt](requirements.txt)で管理します。

パス表記は[ツール共通の規約](../README.md#パス表記)に従います。コマンドはリポジトリのルートから実行し、作業フォルダの変更が必要なスクリプトは後述の表と実行例に従います。

## 変更した翻訳の検査

変更キーを全10言語と非公式訳に照合し、欠落、重複、プレースホルダー、タグ、改行を検査します。比較元は既定で `HEAD`、コミット範囲を調べる場合は `-BaseCommit` で指定します。`-Staged` はステージ済みの内容だけを検査します。
翻訳ファイル全体の空白行も検査します。本文中の改行は `\n` で記述します。

日本語のチェックボックスは、C#の `UI.Toggle` に渡す翻訳キーを抽出し、敬体に揃っているかも検査します。用語と意味は[日本語翻訳方針](../../.codex/PROJECT_GUIDE.md#日本語翻訳方針)に従って原文と照合します。
日本語の変更キーでは、禁止された括弧、括弧の入れ子、コロンの表記も検査します。`-JapaneseGlossaryPath` に指定の用語集を渡すと、活用形や複合語を含めて英語原文から対応語を抽出し、表示名を照合します。`-TerminologyReportPath` で本文の用語照合結果をJSONに保存できます。同綴異義語は用語集のカテゴリと前後の文脈から選びます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\localization\Test-Translations.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\localization\Test-TranslationChecks.ps1
```

2つ目のコマンドは、一時リポジトリを作って検査処理の正常系・異常系を確認し、終了時に撤去します。

## LLMによる翻訳

`tools/localization/.env.example` を同じフォルダの `.env` に複製し、API接続先、APIキー、モデルを設定します。入力パスは `SolastaUnfinishedBusiness/Translations/` を基準に指定します。

```powershell
# 指定ファイルの未訳項目を日本語へ翻訳
python .\tools\localization\translateLLM.py en/Backgrounds-en.txt --target ja

# 指定言語フォルダの未訳項目を他の全言語へ翻訳
python .\tools\localization\translateLLM.py en

# 翻訳先を複数指定
python .\tools\localization\translateLLM.py zh-CN --target en,de,fr

# 既存の訳文も再翻訳
python .\tools\localization\translateLLM.py en/Backgrounds-en.txt --target ja --force
```

処理単位は `BATCH_SIZE`、同時実行数は `MAX_WORKERS`、再試行回数と間隔は `MAX_RETRIES`・`RETRY_DELAY` で指定します。既存の訳文は `--force` を付けたときに上書きします。

## その他のスクリプト

| スクリプト | 作業フォルダ | 処理 |
| --- | --- | --- |
| `translate.py` | `SolastaUnfinishedBusiness/Translations/` | 英語をポルトガル語・スペイン語・フランス語・ドイツ語・イタリア語へ翻訳。`-c` で翻訳先を指定 |
| `translateAutoSync.py` | `SolastaUnfinishedBusiness/Translations/` | 英語にある不足キーを他の9言語へ翻訳して追加 |
| `translateUnofficialAutoSync.py` | リポジトリのルート | `Diagnostics/OfficialTranslations-cn-ZN.txt` から非公式中国語訳を同期 |
| `cleanseTerms.py` | 任意 | 指定フォルダ配下の `.txt` を直接整形・並べ替え。省略時は作業フォルダを処理 |

```powershell
Push-Location .\SolastaUnfinishedBusiness\Translations
try {
    python ..\..\tools\localization\translate.py en -c ja
    python ..\..\tools\localization\translateAutoSync.py -s
} finally {
    Pop-Location
}

python .\tools\localization\translateUnofficialAutoSync.py
python .\tools\localization\cleanseTerms.py .\SolastaUnfinishedBusiness\Translations\ja
```

各スクリプトの引数は `--help` で確認できます。
