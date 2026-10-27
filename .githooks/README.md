# コミットメッセージの検査

独自コミットの件名は日本語で記述します。説明本文を書く場合も、文章段落ごとに日本語を確認します。URL、識別子、コードブロック、標準のtrailerは説明文と区別します。

リポジトリのルートで有効化します。

```powershell
git config --local core.hooksPath .githooks
```

検査の契約テストを実行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\.githooks\Test-CommitMessage.ps1
```
