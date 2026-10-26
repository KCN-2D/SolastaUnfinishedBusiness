# Solasta Unfinished Business KCN-2D カスタム版

`KCN-2D/SolastaUnfinishedBusiness` は、[Solasta Unfinished Business](https://github.com/EnderWiggin/SolastaUnfinishedBusiness) をもとにしたカスタム版です。

作成者がフレンドと Solasta のマルチプレイをしやすくするために調整しています。

このカスタム版を使うことは自由です。ただし、問題が出ても、元の公開版や公式コミュニティへ報告しないでください。

## まず知っておくこと

- Unity Mod Manager は `0.33.2` を使ってください。
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

管理ツール本体を新しい版へ置き換えた場合も、Solasta を選び直して、ゲームに導入済みの Unity Mod Manager を更新してください。

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

## キャラクターをリスペックする

タイトル画面のキャラクター一覧で対象を選び、`リスペック` を押すと、同じレベルまでキャラクターを作り直せます。

## フレンドと設定をそろえる

全員が同じカスタム版を使って、オンラインのロビーへ参加してください。

1. Unity Mod Manager で UB の設定を開き、`ゲームプレイ` → `一般` → `マルチプレイヤー` を開きます。
2. ホストは、参加者の UB とゲーム設定が一致しているか確認します。
3. 設定が違うゲストは、`ホストの設定を保存` を押します。
4. ゲストはロビーから退出してメインメニューへ戻り、同じ設定画面で `MultiplayerHost` を読み込みます。
5. ゲームを終了して起動し直し、ロビーへ再参加します。

画面表示、言語、カメラ、音声、翻訳サービスの個人設定は引き継がれません。読み込む前の設定は、UB フォルダの `Settings.before-import.xml` に保存されます。再起動前なら `設定の読み込みを取り消す` で元に戻せます。

設定確認に対応していない UB や異なるビルドの参加者は、設定画面に表示されます。この確認はゲームの開始を止めないため、全員の一致を確認してから始めてください。XML を書き出して共有する方法も使えます。

## カスタムキャンペーンを日本語で読み上げる

Unity Mod Manager で UB の設定を開き、`ゲームプレイ` → `キャンペーン` で、ナレーションとヒーロー、NPC、調べたときの説明文から読み上げたい項目を有効にします。その下の `日本語の読み上げにVOICEVOXを使います` を有効にし、エンジンの導入ボタンを押します。64ビット版 Windows 向けの最新安定版 CPU エンジンをダウンロードします。ダウンロードは中止・再開できます。`VOICEVOX音声の利用規約` から各話者の規約を確認してください。

UB が導入するのは読み上げ用の VOICEVOX Engine です。導入後は必要に応じて自動起動し、VOICEVOX の設定を無効にするか Solasta を終了すると停止します。すでにこの PC の標準ポート `50021` で VOICEVOX が動いている場合は、そちらに接続します。

接続状態、利用できる音声・演技スタイルの数、本体とエンジンのバージョンが設定画面に表示されます。`VOICEVOXの更新を確認` で更新を調べ、管理中のエンジンはセットアップボタンで更新できます。別途入れた VOICEVOX 本体は `VOICEVOX本体の配布ページ` から更新します。`本体とエンジンの更新を自動確認` も利用できます。

接続できない場合は、設定画面のセットアップ状態を確認してから `音声リストを更新` を押してください。セットアップに失敗した場合は、ディスクの空き容量とインターネット接続を確認して再試行します。VOICEVOX が利用不可でも、通常の読み上げには利用できる同じ言語の Windows 音声や Piper 音声を使います。

ナレーターと各ヒーローの音声は、`自動割り当て` で現在の言語に合う声を選びます。ヒーローは性別とキャラクター画面で選んだ声に合わせて配役されます。UB で個別に選んだ声が優先されます。`ナレーターと全ヒーローを自動割り当てにする` でまとめて切り替えられます。

声を個別に選ぶ場合は、話者を選んでから、音声一覧の検索とページ送りで探します。`音声を試聴します` で確認しながら、速度と声の高さを調整できます。VOICEVOX では各話者のささやきなどの演技スタイルと抑揚も調整でき、抑揚は `1` が標準です。

カスタムキャンペーンを読み込むと、同じ設定画面で NPC ごとの音声を選べます。自動配役は NPC の性別に合う声を優先します。`自動配役に使う声の性別` で声の分類を調整できます。個別設定はそのキャンペーン用に保存され、キャンペーン作者の音声指定や自動配役より優先されます。`既定の割り当てを使います` を選ぶと、作者の指定または自動配役に戻ります。

ダイアログの自動送りは音声の再生完了を待ちます。ダイアログを閉じるかスキップすると、読み上げを停止します。カスタムキャンペーンの雑談も読み上げます。

VOICEVOX を使わず、ダウンロードした日本語の Piper 音声やインストール済みの Windows 音声を選ぶこともできます。ほかの言語でも、その言語に対応する Windows 音声や Piper モデルを使えます。

## うまくいかないとき

まず、次を確認してください。

- Solasta に Unity Mod Manager `0.33.2` を導入している。
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
