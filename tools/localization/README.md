# 翻訳作業ツール

Python 3.9以降を使用します。外部翻訳を使うスクリプトには、既存環境の `deep-translator`、または `openai`・`python-dotenv`・`rich` が必要です。

パス表記は[ツール共通の規約](../README.md#パス表記)に従います。コマンドはリポジトリのルートから実行し、作業フォルダの変更が必要なスクリプトは後述の表と実行例に従います。

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
