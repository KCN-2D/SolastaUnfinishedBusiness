# Runtime verification with a temporary UMM mod

JSON ケースによる回帰テストは `tools/game-test/Invoke-GameTest.ps1`、対象固有のゲーム内プローブには `tools/runtime-verification/Invoke-RuntimeVerification.ps1` を使います。製品コードへ診断処理を入れず、検証用ソース、実行した DLL、結果を対応付けて記録します。

## 配置と保存先

- runner: `tools/runtime-verification/Invoke-RuntimeVerification.ps1`
- UMM harness: `tools/runtime-verification/RuntimeVerificationHarness.cs`
- プローブ: 再利用する C# ソースは `tools/runtime-verification/Probes/`、実行ごとの入力ソースは `tools/game-test/artifacts` 内の該当実行フォルダへ置きます。
- 証跡: `tools/game-test/artifacts/runtime/<timestamp>-<RunName>/`。
- `-EvidenceRoot`で`tools/game-test/artifacts`配下の保存先を指定できます。

生成 DLL と自動生成ソースは証跡ディレクトリの `Build/` にだけ作成し、終了時に削除します。入力として渡したプローブソースは runner が削除しません。一時的な入力ソースの最終整理は作業者が行い、繰り返し必要な検証ソースはローカルで保持します。本書の追加プローブ例を実行する場合は、対象のローカルソースを用意します。証跡はソース管理へ追加しません。

## 実行前の確認

1. 修正前に、報告された経路、同構造の別の項目、正常な例外を決めます。設定の組み合わせ、成功、取り消し、失敗、初回表示、再表示、共有プールへの返却を含めます。
2. `AGENTS.md` と `.codex/PROJECT_GUIDE.md` を確認し、ユーザーの未コミット変更を保持します。通常のゲームプレイ中には実行しません。
3. インストール済みゲームの `Solasta.exe` を明示します。`SolastaInstallDir` も使用できます。参照用の `SolastaFiles` は起動・反映先として使用できません。
4. ゲームを閉じます。runner は既存の `Solasta` プロセスまたは `Mods/UBRuntimeVerification` があれば中断します。以前の検証が残っていた場合は証跡を確認してから復旧し、上書きしません。
5. .NET SDK 9 以降と既存ゲーム/UMM DLL が必要です。新たな本番依存を導入しません。runner はインストール済み SDK の Roslyn を解決するため、開発者固有の SDK パッチ番号を固定しません。

## プローブの契約

同じ検証 DLL へ渡すソースをすべてコンパイルします。各プローブ型には次のどちらか一つを用意します。型・メソッドは public または internal/private で構いません。ネストしていない型を使用してください。

```csharp
public static void Run(Action<string> log)
```

```csharp
public static IEnumerator Run(Action<string> log)
```

`log` へ渡す行の接頭辞で結果を判定します。

- `PASS: ...`: 実際に成立した検証。期待値と実測値が一致した後に出力します。
- `FAIL: ...`: 不一致。例外、Unity の Error/Assert/Exception、プローブの後処理例外も失敗です。
- `INCOMPLETE: ...`: 必要な前提が足りず検証できなかった項目。成功には含めません。
- `INFO: ...`: 寸法、候補数、設定などの証拠。これだけでは成功になりません。

```csharp
using System;
using System.Collections;

public static class ExampleProbe
{
    public static IEnumerator Run(Action<string> log)
    {
        // Capture any in-memory settings and native objects before changing them.
        try
        {
            yield return null; // Wait for the native UI lifecycle when needed.
            bool actualResult = CheckTheBehavior();
            log((actualResult ? "PASS: " : "FAIL: ") + "Describe the observable behavior.");
        }
        finally
        {
            // Unbind and destroy only the native objects created by this probe.
            // Restore in-memory settings changed by this probe.
        }
    }
}
```

例の `CheckTheBehavior()` は検証対象固有の判定に置き換えます。ハーネスは UB の初期化完了後に呼び出し、入れ子の `IEnumerator` も自ら進めて例外を捕捉します。独立して `StartCoroutine` した処理の終了は自動追跡しないため、プローブから必ず終了を待ってください。チェックを一つも報告しないプローブは失敗です。

公開化した参照 DLL の private メンバーは実行時にも public になるわけではありません。実際のゲーム DLL で非公開の API には reflection を使います。テスト用キャラクターや UI は分離して生成し、通常セーブのロード・保存・削除を避けます。既存セーブを使う必要がある場合は依頼範囲と別途バックアップを明確にします。

## 実行

装備やモデルを撮影するプローブは、共通ソース`Probes/PresentationCapture.cs`を`-ProbeSource`へ追加します。`PresentationCapture.Capture(camera, renderers, name, width, height)`を`yield return`で待つと、描画更新後のPNGと`<name>.capture.json`を保存します。カメラ・材質・対象bounds・撮影フレームを記録し、対象が描画範囲外の場合や均一画像の場合は失敗します。カメラとライトは呼び出し元が所有し、撮影処理はカメラの設定とRenderTextureを復元します。

`MeasureSphereSurface(skins, center, radius, influencingBones)`は、現在のスキンをBakeMeshした三角面と球の距離を測ります。`surfaceGap`は正なら面との隙間、負なら交差量です。形状の内部判定は行いません。検査対象の骨と許容値は個別ケースで指定します。共通処理の回帰確認には`Tests/PresentationCaptureProbe.cs`を使用します。

通常は runner にビルド、反映、起動をまとめて行わせます。UB 本体のビルドは必ず指定 wrapper を使い、Roslyn の直接実行は一時検証 DLL だけに限定します。

```powershell
& .\tools\runtime-verification\Invoke-RuntimeVerification.ps1 `
    -RunName Example `
    -ProbeSource 'D:\VerificationWork\ExampleProbe.cs' `
    -ProbeType ExampleProbe `
    -GameExePath 'D:\Games\Solasta\Solasta.exe' `
    -Sync
```

- `-Sync`: 既存の `tools/runtime-verification/Invoke-SolastaSemiAutoTest.ps1 -SkipBuild -Sync` で成果物をゲームへ反映します。修正版 UB は終了後も残します。
- `-SkipBuild`: 直前の指定 wrapper ビルドを再利用するときだけ指定します。使用したビルドログを同じ証跡フォルダへ保存してください。
- `-ValidateOnly`: 既存のビルド成果物を参照してプローブをコンパイルし、入口の型とシグネチャを検証します。ゲームの実行と並行できます。ビルド/同期/ゲーム起動/設定変更は行わず、結果は `incomplete`、`compilationSucceeded=true` です。実行時の検証成功とは扱いません。
- `-ProbeSource` と `-ProbeType`: 配列で複数指定できます。型は指定順に実行します。
- `-ReferencePath`: プローブ固有の追加参照 DLL を明示します。Unity、ゲーム、UMM、UB と基礎 .NET Framework 参照は既定で含みます。
- `-TimeoutSeconds`: 既定 120 秒。起動とプローブ完了までの時間です。時間切れを成功と扱いません。

`-Sync` を省略したときもビルド出力と導入済み UB DLL の SHA-256 が一致していることを要求します。古い DLL を実行して新しいコードの検証済みとは扱いません。

## 追加参照を使うプローブ

リポジトリのルートから実行します。`$gameDirectory` は実際のインストール先へ変更してください。`I2.dll` はそのゲームの `Managed` ディレクトリを参照し、別バージョンをダウンロードしません。

```powershell
$gameDirectory = 'D:\Games\Solasta'
& .\tools\runtime-verification\Invoke-RuntimeVerification.ps1 `
    -RunName ExamplePresentation `
    -ProbeSource 'D:\VerificationWork\ExamplePresentationProbe.cs' `
    -ProbeType ExamplePresentationProbe `
    -ReferencePath (Join-Path $gameDirectory 'Solasta_Data\Managed\I2.dll') `
    -GameExePath (Join-Path $gameDirectory 'Solasta.exe') `
    -Sync
```

プローブ固有の入力、操作、期待結果はケース定義とシナリオ側で管理します。実行ごとの件数、成否、通常処理と差し替えた条件は証跡へ記録します。

## 検証 mod を外した通常起動

プローブの検証後は、同じ runner の `-StartupOnly` で通常起動を確認します。別のスクリプトを手作業で組み立てません。

```powershell
& .\tools\runtime-verification\Invoke-RuntimeVerification.ps1 `
    -RunName NormalStartup `
    -StartupOnly `
    -GameExePath 'D:\Games\Solasta\Solasta.exe' `
    -SkipBuild
```

このパラメータセットでは `-ProbeSource` と `-ProbeType` は不要です。検証 DLL のコンパイル、一時 mod の導入、プローブの実行をしません。ビルド/反映を必要とする場合は通常と同様に `-SkipBuild` を外す/`-Sync` を付けます。設定の生バイト退避/復元、元の Player.log の保全、自分が起動したプロセスだけの終了、証跡保存は共通です。

当該起動で出力された UMM/UB の全マーカーを確認してから 5 秒間、プロセスが終了せずエラーが出ないことを確認します。過去のログのマーカーを流用しません。`success` は通常起動の安定確認だけを意味し、ゲーム内の能力、UI 操作、戦闘、マルチプレイの挙動が検証済みになるわけではありません。

## 不具合修正の受入条件

報告された不具合は、修正前に報告と同じ入口で再現し、修正後に同じシナリオを実行します。画面名、対象、入口、実際の設定、使用した DLL、表示・再生まで到達した結果を記録します。以前のログ、別地点の似た表示、局所プローブの成功を、報告箇所の再現・修正確認の代わりにしません。

- UI の受入確認は、登録済みの画面を通常のメニュー・ボタンの経路から開き、初回表示、アニメーション完了後、閉じて再表示する状態を確認します。別 Canvas への複製、private Bind の直接実行、透明度の固定、modifier のリセット、描画カメラや layer の置換で成立させた表示は局所検証です。文字の寸法だけでなく、実際の描画、透明度、mask、選択・閉じる操作を確認します。
- 音声の受入確認は、報告されたインタラクトから実際に表示された本文、音声要求、設定による音声の解決、実再生の開始・終了までを一続きに追跡します。別の本文を合成できたこと、要求を手でキューへ入れたこと、表示・音声サービスを試験用に置き換えたことは、そのインタラクトが読み上げられる根拠にしません。音声ファイル生成、ゲーム内再生、試聴は区別します。
- UI の復元を検査する期待値は、変更前の native 状態など、製品の保存処理から独立した値にします。製品自身が保存した状態だけを期待値へ読み戻すと、その保存が誤っていても検出できません。
- モデルとモーションは、関節の長さ・結合だけで合格とせず、実際の支持面と表示メッシュの全パーツを照合します。待機、移動開始・停止、前進直後のその場旋回、面の切り替わり、能力発動を連続したフレームで確認し、接地中の滑り、浮き、貫通、踏み替えの位相を区別します。予定した足先と描画した足先の一致は関節の到達性の検査とし、正しい接地の証明とは分けます。床と壁の境目では両方の面を調べ、実際のColliderの範囲外へ延長した平面で貫通と判定しません。開始・終了の静止姿勢だけで移動中も正常とは扱わず、共通の観測経路で全区間を確認します。
- 動作の計測は、画像保存で処理が止まる区間と通常の描画速度の区間を分けます。停止後の姿勢を判定する待機時間はゲーム時間で固定し、合格するまで待つ条件にはしません。接地中の足は、実支持面に対する足裏の位置・上下変化・連続したフレームから識別し、製品の状態フラグや中立姿勢そのものを接地の期待値にしません。
- 既存セーブの読み込みが必要な場合は、依頼対象をバックアップし、保存・自動保存が原本を変更しない方法を明示します。実サービスや設定の差し替えが対象動作の前提を変える場合、その結果は実セーブからの受入確認に含めません。

局所プローブは条件分岐・例外・後始末の回帰確認として使います。実行した型と入口、差し替えた前提、確認していない範囲を結果に記録し、ユーザーシナリオの結果と分けます。大量の同条件反復やアサーション合計件数を、実操作を確認した根拠にしません。受入シナリオが未実施・失敗の場合は、ビルドや局所プローブが成功していても修正完了と報告しません。

## 自動保全と終了処理

runner は起動前に元の `Player.log` を `Original-Player.log` に保存し、UB の `Settings.xml` と UMM の `Params.xml` を生バイトで退避します。UMM の起動時表示だけを一時的に無効化し、プローブ実行時に限り検証 mod を追加します。既存キャラクターやセーブは通常操作しません。受入シナリオに実ロードが必要な場合は、上記の原本保全と依頼範囲の確認を適用します。

`-ShowGameWindow` はWindows VMゲスト内の画面操作の受入確認に使います。ゲームと操作runnerを同じVM内で実行し、ホストからVM画面をクリックする操作は使いません。VM内の3D描画・画面取得・入力は未検証で、追加導入は事前に確認します。

入力不要のプローブは `-RunInBackground` を指定すると、一時harnessがメインスレッドで `Application.runInBackground` を有効にし、完了・破棄時に元へ戻します。harnessを配置しない `-StartupOnly` とは併用できません。

通常回帰は `tools/game-test/Invoke-GameTest.ps1` を使います。直接このrunnerを使う入力不要のプローブでは、`-RunInBackground -RequireOtherAppForeground` を指定します。実行ごとに非入力のWindowsデスクトップを作り、その中でGPU描画するゲームウィンドウを起動します。ホストのデスクトップは切り替えず、`-batchmode` と `-nographics` は指定しません。`-ShowGameWindow` と `-StartupOnly` とは併用できません。

メインスレッドの`Runtime-Desktop.json`と継続観測`Runtime-Desktop.jsonl`を、起動時のPID・native thread・デスクトップと照合します。`UOI_IO`は取得成功と出力BOOLを分離し、取得失敗を非入力状態の根拠にしません。ホストの前面HWND・PID・所有threadと入力デスクトップも500ミリ秒ごとに読み取り、ゲームが前面になった場合や必要な情報を取得できない場合は終了・復元し、判定を`incomplete`にします。

前面ウィンドウの変化と1秒ごとの記録は`Runtime-Foreground.jsonl`、メインスレッドのフォーカス・GPU・フレーム・時間倍率・Screen寸法は`Runtime-Execution.jsonl`に保存します。`-RequireNativeActionProof`は、保存済みゲームのaction開始・終了と、その間の一意のrender窓を照合します。`game-test`の通常回帰ではこの条件を必須にします。

Unityの実フォーカス喪失はOS入力の非占有と別に判定します。`game-test`の`verifyFocusLoss=false`は非占有native runnerの範囲で、実フォーカス喪失は未検証です。`verifyFocusLoss=true`は、実測したtrueからfalseへの遷移があり、`Application.isFocused=false`中にアクションが進行した場合だけ成立します。フォーカス通知の引数とWindowsのアクティブ・入力先ウィンドウも記録します。通常UI入力を迂回した操作は、そのUIの受入確認には含めません。音声イベントの受理と、実際の出音・音質も別に判定します。

save-smokeの画像取得は全RGBが0のフレームを除外し、5秒またはケース期限まで再取得します。`GameTest-Captures.jsonl` に採否とハッシュを記録します。

このrunner、共通harness、`WindowsDesktop.ps1` / `.cs`、表示設定の退避・復元、表示観測と画像一覧の共通プローブ、READMEはGitで管理します。個別プローブ、ケース、証跡、一時ファイルは追跡しません。

終了・失敗・時間切れはすべて同じ `finally` を通ります。

1. 自分が起動したプロセスだけを、開始時刻と実行ファイルを照合して閉じます。`CloseMainWindow()` から 5 秒待ち、終了しない場合に限りそのプロセスを停止します。
   既に終了を検知している場合も、`WaitForExit`で終了シグナルを最大5秒待ちます。成功を確認してから、その実行が作成したデスクトップとプロセスハンドルを解放します。
2. `Runtime-Player.log` と結果を保存します。UMM の runtime 版と `n/n MODS`、UB の Loading/Active/enabled マーカー、例外・エラーを確認します。
3. 設定を生バイトで復元して一致を確認します。もともと存在しなかった設定ファイルは削除します。
4. この実行が作成した `Mods/UBRuntimeVerification` と生成用 `Build/` を削除します。削除対象の絶対パスが既知の親ディレクトリ内か検証し、reparse point を経由した再帰削除を拒否します。
5. `Result.json` を保存します。

プロセス終了に失敗した場合は、実行中ゲームが再保存する可能性があるため設定復元と mod 削除を見送って失敗として記録します。`SettingsBackup/` を残し、ゲーム終了後にその内容を元のパスへ生バイトで戻して一時 mod を撤去します。runner 自体を強制終了した場合も同じ復旧が必要です。並行して別の実行を始めません。

## 証跡と完了条件

主な証跡は次のとおりです。

- `Result.json`: 最終判定、実行時刻、プローブソースの原パス/ハッシュと `archivePath`、ビルド/導入済み/検証 DLL とゲーム DLL のハッシュ、設定復元・一時 mod 撤去の結果。
- `InputSources/`: 実行受付時に保存したプローブ入力。そのハッシュを受付時とコンパイル直前に照合し、保存した複製だけをコンパイルします。原本を後で編集しても実行内容は変わりません。`Build/` の削除後も保持し、修正前後比較では同じ入力ハッシュを使います。
- `Runtime-Assembly.json`: 実AssemblyとUMM ModEntryの参照、元DLLとUMMキャッシュのSHA-256、実module MVIDをformal buildと照合した結果。実際の読込パスを保持します。
- `Runtime-ProcessCleanup.json`: 所有プロセスのPID・開始時刻・実行パス、取得失敗、終了要求と各待機の結果・所要時間。
- `SettingsBackup/Native-DisplaySettings.json`: UnityがXMLとは別に保存する表示用レジストリの型と値。ゲーム終了後に照合して復元します。
- `Build.log`、`Sync.log`、`Compile.log`: 各工程の実行結果。
- `Probe.log`、`HarnessResult.json`: 項目別検証とハーネス内集計。
- `Original-Player.log`、`Runtime-Player.log`: 修正前のログの保全と当該実行のログ。
- `Runner-error.log`、`Runtime-errors.log`: 失敗時の根拠。
- `SettingsBackup/`: 強制中断時も復旧できる設定の原本。第三者へ共有する前には個人情報・秘密情報を確認します。

`success` は実行したプローブの範囲で、少なくとも一つの成立したチェックがあり、失敗・未完了がなく、起動・設定復元・撤去にも成功した場合だけです。`failed` は不一致、例外、コンパイル/起動/復元/撤去の失敗です。`incomplete` は時間切れ、必要な前提不足、またはコンパイルのみの確認です。実行を要求した場合の `failed` と `incomplete` は非 0 終了になります。

`game-test`は実行範囲を`acceptanceScope`に記録します。[scopeの一覧](../game-test/README.md#結果と復元)に従い、プローブの成功と通常UI・実フォーカス喪失・可聴音の確認を分けます。[Blender工程内検証](../asset-authoring/README.md)の成功も、Unity資産ビルドや実ゲームでの資産統合の成功へ広げません。依頼全体の必須項目と完了条件は、各工程の結果とは別に確認します。

Harmony patch の追加・統合では、読み込んだゲーム DLL に対する対象型・オーバーロードの解決と、UB が登録した patch の段階・件数・実行順を確認します。同じメソッドへの意図しない二重登録、`__state` と Finalizer の対応漏れを検出し、対象の正常経路と例外後の復元も検証します。確認結果は既存の `Probe.log` 等へ記録し、専用のチェックリスト文書を増やしません。ファイル配置の判断と追加ファイルの棚卸しは `.codex/PROJECT_GUIDE.md` に従います。

UI 修正はデータの Bind だけで終わらせず、Show 後の複数フレーム、各選択肢の画面内座標、選択操作、解像度/UI 倍率、スクロール到達性、再利用後の復元を確認します。ルール修正では最終状態だけでなく、その行動による効果、リソース消費、取り消し時の復元を確認します。自動検証後の `-StartupOnly` による通常起動と、必要に応じた実プレイ・マルチプレイの手動確認は別の確認項目です。未実施の項目は明示してください。

完了報告には証跡フォルダへのリンクと検証した範囲を示します。確認に必要なログはユーザーが修正を確認するまで保持し、不要になった生成物と一時ソースを整理します。既存の証跡を TEMP から移す場合は永続保存先へコピー後にハッシュ一致を確認し、移行元を削除します。
