# Solasta Unfinished Business KCN-2D カスタム版

`KCN-2D/SolastaUnfinishedBusiness` は、[Solasta Unfinished Business](https://github.com/EnderWiggin/SolastaUnfinishedBusiness) をもとにしたカスタム版です。

作成者がフレンドと Solasta のマルチプレイをしやすくするために調整しています。

このカスタム版を使うことは自由です。ただし、問題が出ても、元の公開版や公式コミュニティへ報告しないでください。

## まず知っておくこと

- Unity Mod Manager は `0.32.4a` を標準にしています。
- 古い Unity Mod Manager は優先しません。安定しない場合は `0.32.4a` を使ってください。
- 既存のセーブデータ、キャラクター、設定は先にバックアップしてください。

## 用意するもの

- Solasta: Crown of the Magister
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
- GitHub Releases にある `SolastaUnfinishedBusiness.zip`

Unity Mod Manager は、Solasta にカスタム版を読み込ませるために必要です。省略はできません。

## Unity Mod Manager を入れる

1. [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) を開きます。
2. Nexus Mods にログインします。
3. `Files` を開きます。
4. `Manual Download` を押します。
5. ダウンロードしたファイルを好きな場所に展開します。
6. 展開したフォルダの `UnityModManager.exe` を起動します。
7. 一覧から `Solasta` を選びます。
8. Solasta が入っているフォルダを選びます。
9. `Install` を押します。
10. エラーが出なければ、Solasta を起動します。
11. ゲーム起動時に Unity Mod Manager の画面が出るか確認します。

Solasta のフォルダが分からない場合は、Steam から確認できます。

1. Steam のライブラリで Solasta を右クリックします。
2. `管理` を選びます。
3. `ローカルファイルを閲覧` を選びます。
4. 開いたフォルダを Unity Mod Manager で選びます。

## カスタム版を入れる

1. GitHub Releases から `SolastaUnfinishedBusiness.zip` をダウンロードします。
2. Unity Mod Manager を起動します。
3. `Mods` の画面を開きます。
4. `SolastaUnfinishedBusiness.zip` を Unity Mod Manager の画面へドラッグします。
5. 一覧に `SolastaUnfinishedBusiness` が出たら成功です。
6. Solasta を起動します。
7. Unity Mod Manager の画面で、このカスタム版が有効になっているか確認します。

すでに古い版を入れている場合は、Unity Mod Manager の画面から入れ直してください。

## うまくいかないとき

まず、次を確認してください。

- Unity Mod Manager `0.32.4a` を使っている。
- Solasta のフォルダを正しく選んでいる。
- `SolastaUnfinishedBusiness.zip` を展開せず、そのまま Unity Mod Manager に入れている。
- Solasta を起動したとき、Unity Mod Manager の画面が出ている。
- Unity Mod Manager の画面で、このカスタム版が有効になっている。

それでも直らない場合は、このカスタム版を共有した相手に確認してください。

元の公開版や公式コミュニティへは報告しないでください。

確認を頼むときは、分かる範囲で次の情報を添えてください。

- Solasta のバージョン
- Unity Mod Manager のバージョン
- どの画面、セーブ、キャラクターで起きたか
- 何をしたら起きたか
- `Player.log`
- 関係するセーブデータ、キャラクターファイル、`Settings.xml`

`Player.log` は、通常このフォルダにあります。

```text
C:\Users\<ユーザー名>\AppData\LocalLow\Tactical Adventures\Solasta
```

## 参考リンク

- 元の公開版: <https://github.com/EnderWiggin/SolastaUnfinishedBusiness>
- KCN-2D 版: <https://github.com/KCN-2D/SolastaUnfinishedBusiness>
- Unity Mod Manager: <https://www.nexusmods.com/site/mods/21>

## ライセンス

ライセンスは、このリポジトリの `LICENSE` を参照してください。
