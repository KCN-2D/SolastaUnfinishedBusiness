# コミットメッセージの検査

独自コミットの件名は日本語で記述します。説明本文を書く場合も、文章段落ごとに日本語を確認します。URL、識別子、コードブロック、標準のtrailerは説明文と区別します。

リポジトリのルートで有効化します。

```powershell
git config --local core.hooksPath .githooks
```

`commit-msg`がGit for WindowsからWindows PowerShellを起動し、`commit-msg.ps1`でUTF-8のメッセージを検査します。条件を満たさない場合はコミットを中止します。MergeやRevertも同じ条件で検査します。

入口を通した検証は次で実行します。Gitの設定や履歴は変更しません。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\.githooks\Test-CommitMessage.ps1
```
