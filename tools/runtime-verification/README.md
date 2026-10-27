# 個別プローブと実行基盤

固定ケースによる回帰テストと配置前の状態への復元は[実ゲーム回帰テスト](../game-test/README.md)を使います。本書は、対象固有のプローブ・通常起動の確認と、共通harnessの契約・観測・後始末を扱います。パス表記は[ツール共通の規約](../README.md#パス表記)に従います。

## 配置と保存先

- runner: `tools/runtime-verification/Invoke-RuntimeVerification.ps1`
- UMM harness: `tools/runtime-verification/RuntimeVerificationHarness.cs`
- プローブ: `tools/runtime-verification/Probes/`。機能ごとのローカルシナリオは `tools/game-test/Scenarios/`、実行ごとの入力ソースは `tools/game-test/artifacts/` 内の該当実行フォルダへ置きます。
- 証跡: `tools/game-test/artifacts/runtime/<timestamp>-<RunName>/`。
- `-EvidenceRoot`で`tools/game-test/artifacts`配下の保存先を指定できます。

実行名が長い場合は保存フォルダ名だけを短縮し、元の名前をメタデータに残します。ネイティブの一時キャラクターファイルまで収まらない`EvidenceRoot`は、配置・起動前に必要な長さを示して拒否します。

生成DLLと自動生成ソースは証跡フォルダの `Build/` にだけ作成し、終了時に削除します。入力として渡したプローブソースはrunnerが削除しません。一時的な入力ソースの最終整理は作業者が行い、繰り返し必要な検証ソースはローカルで保持します。

## 実行前の確認

1. [作業ガイド](../../.codex/PROJECT_GUIDE.md)を確認します。
2. インストール済みゲームの `Solasta.exe` を明示します。`SolastaInstallDir` も使用できます。参照用の `SolastaFiles` は起動・反映先として使用できません。
3. ゲームを閉じます。runnerは既存の `Solasta` プロセスまたはインストール先の `Mods/UBRuntimeVerification/` があれば中断します。以前の検証が残っていた場合は証跡を確認してから復旧し、上書きしません。
4. .NET SDK 9 以降と既存ゲーム/UMM DLL が必要です。

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

公開化した参照 DLL の private メンバーは実行時にも public になるわけではありません。実際のゲーム DLL で非公開の API には reflection を使います。既存セーブを使う検証は[game-test](../game-test/README.md)から実行します。

## 実行

装備やモデルを撮影するプローブは、共通ソース`tools/runtime-verification/Probes/PresentationCapture.cs`を`-ProbeSource`へ追加します。`PresentationCapture.Capture(camera, renderers, name, width, height)`を`yield return`で待つと、描画更新後のPNGと`<name>.capture.json`を保存します。カメラ・材質・対象bounds・撮影フレームを記録し、対象が描画範囲外の場合や均一画像の場合は失敗します。カメラとライトは呼び出し元が所有し、撮影処理はカメラの設定とRenderTextureを復元します。
画面外のスキンを所有カメラで撮る場合は`warmupFrames: 2`を指定し、通常の描画を通してから撮影します。`onCaptured`はRender直後の同フレームに実行する観測用callbackです。既定のwarmupは0フレームで、使用数と撮影フレームはreceiptへ記録します。

`MeasureSphereSurface(skins, center, radius, influencingBones)`は、現在のスキンをBakeMeshした三角面と球の距離を測ります。`surfaceGap`は正なら面との隙間、負なら交差量です。形状の内部判定は行いません。検査対象の骨と許容値は個別ケースで指定します。共通処理の回帰確認には`tools/runtime-verification/Tests/PresentationCaptureProbe.cs`を使用します。

リポジトリのルートからrunnerにビルド、反映、起動をまとめて行わせます。UB本体は[正式ビルド](../CustomBuildLocal/README.md)を使い、Roslynの直接実行は一時検証DLLだけに限定します。入力不要のプローブには、次の非入力デスクトップ指定を付けます。

```powershell
& .\tools\runtime-verification\Invoke-RuntimeVerification.ps1 `
    -RunName Example `
    -ProbeSource 'D:\VerificationWork\ExampleProbe.cs' `
    -ProbeType ExampleProbe `
    -GameExePath 'D:\Games\Solasta\Solasta.exe' `
    -Sync -RunInBackground -RequireOtherAppForeground
```

- `-Sync`: [半自動確認](#成果物の同期と起動確認)を介して成果物をゲームへ反映します。修正版UBは終了後も残します。配置前の版へ戻す検証には`game-test`を使います。
- `-SkipBuild`: 直前の指定 wrapper ビルドを再利用するときだけ指定します。使用したビルドログを同じ証跡フォルダへ保存してください。
- `-ValidateOnly`: 既存のビルド成果物を参照してプローブをコンパイルし、入口の型とシグネチャを検証します。ゲームの実行と並行できます。ビルド/同期/ゲーム起動/設定変更は行わず、結果は `incomplete`、`compilationSucceeded=true` です。
- `-ProbeSource` と `-ProbeType`: 配列で複数指定できます。型は指定順に実行します。
- `-ReferencePath`: プローブ固有の追加参照 DLL を明示します。Unity、ゲーム、UMM、UB と基礎 .NET Framework 参照は既定で含みます。
- `-TimeoutSeconds`: 既定 120 秒。起動とプローブ完了までの時間です。

`-Sync` を省略したときもビルド出力と導入済み UB DLL の SHA-256 が一致していることを要求します。

## 追加参照を使うプローブ

リポジトリのルートから実行します。`$gameDirectory` は実際のインストール先へ変更してください。`I2.dll` はそのゲームの `Managed` ディレクトリを参照します。

```powershell
$gameDirectory = 'D:\Games\Solasta'
& .\tools\runtime-verification\Invoke-RuntimeVerification.ps1 `
    -RunName ExamplePresentation `
    -ProbeSource 'D:\VerificationWork\ExamplePresentationProbe.cs' `
    -ProbeType ExamplePresentationProbe `
    -ReferencePath (Join-Path $gameDirectory 'Solasta_Data\Managed\I2.dll') `
    -GameExePath (Join-Path $gameDirectory 'Solasta.exe') `
    -Sync -RunInBackground -RequireOtherAppForeground
```

プローブ固有の入力、操作、期待結果はケース定義とシナリオ側で管理します。実行ごとの件数、成否、通常処理と差し替えた条件は証跡へ記録します。

## 検証 mod を外した通常起動

プローブの検証後は、同じ runner の `-StartupOnly` で通常起動を確認します。

```powershell
& .\tools\runtime-verification\Invoke-RuntimeVerification.ps1 `
    -RunName NormalStartup `
    -StartupOnly `
    -GameExePath 'D:\Games\Solasta\Solasta.exe' `
    -SkipBuild
```

このパラメータセットでは `-ProbeSource` と `-ProbeType` は不要です。検証 DLL のコンパイル、一時 mod の導入、プローブの実行をしません。ビルド/反映を必要とする場合は通常と同様に `-SkipBuild` を外す/`-Sync` を付けます。UB・UMM設定の退避/復元、元の Player.log の保全、自分が起動したプロセスだけの終了、証跡保存は共通です。通常起動は本来のユーザーデータ保存先を使います。固定したネイティブ設定を使う検証はプローブ経由で実行します。

当該起動で出力された UMM/UB の全マーカーを確認してから 5 秒間、プロセスが終了せずエラーが出ないことを確認します。

## 成果物の同期と起動確認

`tools/runtime-verification/Invoke-SolastaSemiAutoTest.ps1` は、ビルド成果物の確認・同期・起動を行う補助入口です。既定では正式ビルドと成果物確認だけを行い、次の指定を明示した場合だけゲームへ変更を加えます。プローブや設定の自動退避・復元が必要な確認には、前節までのrunnerを使います。

| 指定 | 動作 |
| --- | --- |
| `-SkipBuild` | 直前の正式ビルドを再利用 |
| `-Sync` | インストール先の `Mods/SolastaUnfinishedBusiness/` へ成果物を上書きコピー。`Settings.xml` と `Settings/*.xml` は保持し、同期した製品版は残す |
| `-Launch` | `Solasta.exe` を起動し、今回の `Player.log` を判定 |
| `-GameExePath` または環境変数 `SolastaInstallDir` | 同期・起動対象のインストール先を明示。リポジトリの `SolastaFiles/` は対象にできない |
| `-UmmStagingDirectory` | `-Sync` でUMMのstagingへZIPもコピーする場合に指定 |

同期・起動時にはUMM runtimeを確認します。`-Launch`は今回の起動で生成・追記されたログを判定します。成功にはUMMの `n/n MODS`（`n > 0`）とUBの `Version ... Loading.`、`Active.`、`enabled in ...` の全マーカーが必要です。UB error、UMMの不一致、プロセスの早期終了、timeoutは非0終了になります。

## 検証の記録

実行した型・入口、入力条件、製品DLL、観測結果を`Probe.log`と`Result.json`に保存します。実ゲームでの判定と復元は[game-testの結果](../game-test/README.md#結果と復元)、実装・表示の確認基準は[作業ガイド](../../.codex/PROJECT_GUIDE.md)に従います。

## 検証用ユーザーデータの隔離

プローブ実行中は設定・出力先を `tools/game-test/artifacts/.native/<所有ID>/` へ切り替え、`Native-UserDataLocation.json` と所有マーカーに記録します。UBが設定を読む前に一時harnessを起動するため、検証中だけ導入済みUBの `Info.json` に一時modへの依存を追加します。既存の依存条件を保ち、依存・`LoadAfter` の循環は起動前に拒否します。`Info.json` は終了後に復元します。

User/Platform設定と、読込に必要な `UserContent/`・`TempSessionData/` を作業用保存先へ複製します。セーブ・キャラクターの出力先は空の作業用領域です。固定設定を渡す場合は `-NativePreferencesInputDirectory` に両XMLのあるフォルダーを指定します。game-testはこの指定をケースから渡します。元の設定ファイルは置き換えません。

プローブ終了後も、nativeの終了処理による出力が終わるまで作業用保存先を維持します。所有マーカーと起動プロセスのnonceが一致しない場合は実行を止めます。元の設定・セーブ・キャラクターは起動前後で内容と更新時刻を照合し、不一致は失敗として残します。元ファイルへの自動上書きはしません。`CrashCheckFile.txt` はmod読込前にnativeが作るため、既存の退避・復元で保護します。

`Native-UserData.json` にゲーム内での切替え、`Native-UserDataInputs.json` に入力、`Native-OriginalFiles-Before.json` と `Native-OriginalFiles-After.json` に元ファイルの照合結果を記録します。指定した `InstallStartupHooks` もUBの読込前に呼ばれます。

## 実行デスクトップと観測

`-ShowGameWindow` は[Windows VM内での画面操作の受入確認](../game-test/README.md#画面操作の受入確認)に使います。

入力不要のプローブは `-RunInBackground` を指定すると、一時harnessがメインスレッドで `Application.runInBackground` を有効にし、完了・破棄時に元へ戻します。harnessを配置しない `-StartupOnly` とは併用できません。

通常回帰は `tools/game-test/Invoke-GameTest.ps1` を使います。直接このrunnerを使う入力不要のプローブでは、`-RunInBackground -RequireOtherAppForeground` を指定します。実行ごとに非入力のWindowsデスクトップを作り、その中でGPU描画するゲームウィンドウを起動します。ホストのデスクトップは切り替えず、`-batchmode` と `-nographics` は指定しません。`-ShowGameWindow` と `-StartupOnly` とは併用できません。

メインスレッドの`Runtime-Desktop.json`と継続観測`Runtime-Desktop.jsonl`にPID・native thread・所有HWND・デスクトップ・入力状態を記録し、起動時の情報と照合します。`UOI_IO`はAPI取得成功、必要バイト数、生BOOL、nullable値を分け、取得失敗を非入力状態の根拠にしません。ホストの前面HWND・PID・所有threadと入力デスクトップも500ミリ秒ごとに読み取り、ゲームが前面になった場合や必要な情報を取得できない場合は終了・復元し、判定を`incomplete`にします。親の入力デスクトップが実行前後で変わっていないことも確認します。

前面ウィンドウの変化と1秒ごとの記録は`Runtime-Foreground.jsonl`、メインスレッドのフォーカス・GPU・フレーム・時間倍率・Screen寸法は`Runtime-Execution.jsonl`に保存します。`-RequireNativeActionProof`は、保存済みゲームのaction開始・終了、GPUとフレーム進行、Screenとclient寸法が一致する一意のゲームウィンドウを照合します。`game-test`の通常回帰ではこの条件を必須にします。

Unityの実フォーカス喪失はOS入力の非占有と別に判定し、フォーカス通知の引数とWindowsのアクティブ・入力先ウィンドウも記録します。`verifyFocusLoss`の指定方法と判定範囲は[実ゲーム回帰の実行手順](../game-test/README.md#明示配置と実行)と[結果の分類](../game-test/README.md#結果と復元)に従います。

## 自動保全と終了処理

runnerは起動前に元の `Player.log` を `Original-Player.log` に保存し、UBの `Settings.xml` とUMMの `Params.xml` を生バイトで退避します。UMMの起動時表示だけを一時的に無効化し、プローブ実行時に限り検証modを追加します。

終了・失敗・時間切れはすべて同じ `finally` を通ります。

1. 自分が起動したプロセスだけを、開始時刻と実行ファイルを照合して閉じます。`CloseMainWindow()` から 5 秒待ち、終了しない場合に限りそのプロセスを停止します。
   既に終了を検知している場合も、`WaitForExit`で終了シグナルを最大5秒待ちます。成功を確認してから、その実行が作成したデスクトップとプロセスハンドルを解放します。
2. `Runtime-Player.log` と結果を保存します。UMM の runtime 版と `n/n MODS`、UB の Loading/Active/enabled マーカー、例外・エラーを確認します。
3. 設定を生バイトで復元して一致を確認します。もともと存在しなかった設定ファイルは削除します。
4. インストール先にこの実行が作成した `Mods/UBRuntimeVerification/` と証跡内の生成用 `Build/` を削除します。削除対象の絶対パスが既知の親ディレクトリ内か検証し、reparse pointを経由した再帰削除を拒否します。
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
- `SettingsBackup/`: 強制中断時も復旧できる設定の原本。

`success` は実行したプローブの範囲で、少なくとも一つの成立したチェックがあり、失敗・未完了がなく、起動・設定復元・撤去にも成功した場合だけです。`failed` は不一致、例外、コンパイル/起動/復元/撤去の失敗です。`incomplete` は時間切れ、必要な前提不足、またはコンパイルのみの確認です。実行を要求した場合の `failed` と `incomplete` は非 0 終了になります。

`game-test`が記録する実行範囲`acceptanceScope`は、[結果と復元](../game-test/README.md#結果と復元)を参照してください。

証跡の整理は、[証跡の容量管理](../game-test/README.md#証跡の容量管理)に従います。このrunner単独の証跡は、対応する `.native/<所有ID>/` も一緒に保持します。不要になった場合は復元を確認し、`Native-UserDataLocation.json` と両所有マーカーが一致する保存先を特定してから、実行フォルダと対で整理します。

## Gitの追跡範囲

runner・同期入口・共通harness・デスクトップと表示設定の管理、`Probes/DisplayStateProbe.cs`・`Probes/PresentationCapture.cs`、`Tests/PresentationCaptureProbe.cs`とREADMEを追跡します。個別プローブ、シナリオ、テストケース、証跡、一時ファイルは追跡しません。固定ケースの配置は[game-test](../game-test/README.md#gitの追跡範囲)に従います。
