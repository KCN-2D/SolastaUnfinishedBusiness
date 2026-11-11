# Solasta Unfinished Business KCN-2D カスタム版

`KCN-2D/SolastaUnfinishedBusiness` は、[Solasta Unfinished Business](https://github.com/EnderWiggin/SolastaUnfinishedBusiness) をもとにしたカスタム版です。

フレンドと Solasta のマルチプレイをしやすいように調整しています。

## 用意するもの

- Solasta: Crown of the Magister
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) `0.33.2`
- カスタム版の `SolastaUnfinishedBusiness.zip`

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
10. Solasta を起動します。
11. ゲーム起動時に Unity Mod Manager の画面が出るか確認します。

管理ツール本体を新しい版へ置き換えた場合も、Solasta を選び直して、ゲームに導入済みの Unity Mod Manager を更新してください。

Solasta のフォルダが分からない場合は、Steam から確認できます。

1. Steam のライブラリで Solasta を右クリックします。
2. `管理` を選びます。
3. `ローカルファイルを閲覧` を選びます。
4. 開いたフォルダを Unity Mod Manager で選びます。

## カスタム版を入れる

旧版から更新するときは、Solasta を終了し、ゲームフォルダーの `Mods/SolastaUnfinishedBusiness/BlenderModels/CharacterFaces` が残っていれば削除します。

1. Unity Mod Manager を起動します。
2. `Mods` の画面を開きます。
3. `SolastaUnfinishedBusiness.zip` を Unity Mod Manager の画面へドラッグします。
4. 一覧に `SolastaUnfinishedBusiness` が出たら成功です。
5. Solasta を起動します。
6. Unity Mod Manager の画面で、このカスタム版が有効になっているか確認します。

自分で ZIP を作成する手順は、[ローカルビルド](tools/CustomBuildLocal/README.md)を参照してください。

## フレンドと遊ぶ

全員が同じカスタム版とゲーム設定を使って、オンラインのロビーへ参加してください。

UB設定の「ゲームプレイ → 一般 → マルチプレイ」で、探索中の長時間行動を共同予約できます。シングルプレイでも利用できます。

1. 「探索中の長時間行動を予約する」を有効にします。
2. 儀式や〈予知〉などの呪文、激励する指揮官などの能力で、発動前に「冒険の支度」が開きます。各仲間の「行動を選ぶ」から、その仲間の呪文・能力欄へ戻って支度を追加できます。
3. 行動を確認し、各プレイヤーが「準備完了」を押します。
4. 「始める」を押します。マルチプレイではホストが開始します。

各キャラクターに1行動を予約できます。行動は同時に開始し、短い行動から完了します。パーティ全体の経過時間は、最も長い行動の所要時間です。行動を変更した場合は、全員がもう一度「準備完了」を押します。

自分が操作する召喚クリーチャーの行動も選べます。

閉じた画面は、探索画面の「冒険の支度」から開き直せます。

ロード完了の自動確認、高速移動への自動賛成、仲間への自動追従、探索中のアイテム受け渡し距離の拡張も個別に選べます。自動追従を有効にすると、冒険の支度画面で追従する仲間を選べます。自分で移動すると追従を解除します。

## 参考リンク

- 元の公開版: <https://github.com/EnderWiggin/SolastaUnfinishedBusiness>
- KCN-2D 版: <https://github.com/KCN-2D/SolastaUnfinishedBusiness>
- Unity Mod Manager: <https://www.nexusmods.com/site/mods/21>

## ライセンス

ライセンスは、このリポジトリの `LICENSE` を参照してください。
