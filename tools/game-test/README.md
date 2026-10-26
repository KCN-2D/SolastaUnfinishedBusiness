# 実ゲーム回帰テスト

固定ケースによる回帰、検証版の配置、結果の判定、配置前の状態への復元を扱います。[正式ビルドの前提](../CustomBuildLocal/README.md)に加え、導入済みのSolastaとUMM 0.33.2以降が必要です。リポジトリのルートでPowerShellを開き、以下の`$game`を実際のインストール先へ変更して実行します。パス表記は[ツール共通の規約](../README.md#パス表記)に従います。

## 診断とビルド

```powershell
$game = 'D:\Games\Solasta'
& .\tools\game-test\Test-GameTestEnvironment.ps1 -GameExePath "$game\Solasta.exe"
$build = & .\tools\game-test\New-GameTestBuild.ps1 -GameExePath "$game\Solasta.exe"
```

`TargetCommit` は実在するコミットへ解決し、現在の `HEAD` と一致することを要求します。ビルド対象はそのコミットに作業ツリーの変更を重ねた内容です。別コミットを確認する場合は、そのコミットのチェックアウトで実行します。

ビルドは[正式ビルド](../CustomBuildLocal/README.md)を呼び、成果物を検証ごとの証跡フォルダにある `Stage/SolastaUnfinishedBusiness/` へ複製して、全ファイルのハッシュを記録します。`Stage/` など実行ごとの入出力パスは、その証跡フォルダが基準です。

配布ZIPのハッシュは `Build.json` に記録し、証跡への重複コピーは省きます。ZIPも証跡へ残す場合は `New-GameTestBuild.ps1` に `-KeepPackageArchive` を追加します。

`Build.json` にブランチ、対象コミット、ステージ済み差分、作業ツリー差分、変更ファイル、ゲーム・UMM DLL、wrapper、runtime patch、成果物の識別情報を保存します。`tools/` は製品ソースの識別から除き、ビルド入力とテストツールをそれぞれのハッシュで記録します。ビルド中のソース変更、実行時のソース・ゲーム DLL・ステージングの不一致は中断理由になります。

テスト基盤を変更した場合は、`& .\tools\game-test\Test-GameTestContracts.ps1`で停止ガード、終了処理、証跡整理の安全性を検証します。保存した実装の原本を使い、プロセス取得失敗・復元拒否、所有するPowerShell子プロセスの終了・待機、ダミーファイルでの整理対象と保持条件を確認します。ゲームや設定は変更せず、結果は`tools/game-test/artifacts/`へ保存します。`-SkipProcessLifecycle`は子プロセスの終了・待機の検証を省略します。

## ケースを固定する

```powershell
$save = '<検証に使うセーブの絶対パス>'
$case = & .\tools\game-test\New-GameTestCase.ps1 `
    -SavePath $save -SettingsFile "$game\Mods\SolastaUnfinishedBusiness\Settings.xml" `
    -OutputDirectory .\tools\game-test\Cases\save-move -LoadedPartyCount 1
```

新しい `OutputDirectory` に UB 設定とネイティブ設定を固定し、セーブ原本のハッシュをケースへ記録します。実行時にはセーブを証跡内へ複製します。通常のセーブ・キャラクター出力先とクラウド書き込みは、固定プローブが一時的に隔離します。

JSON の主要項目は次のとおりです。

| 項目 | 内容 |
| --- | --- |
| `schemaVersion`, `id`, `adapter` | 形式、ケース名、固定adapter名 |
| `initialState` | セーブと設定の絶対パス、SHA-256 |
| `operation` | 操作種別、`actorSource`（`party` / `guest`）、その一覧内のインデックス、画面操作待ち、`verifyFocusLoss`（既定 `false`）。`move`では移動量も指定 |
| `expected` | 人数、ユーザーロケーション、探索状態の期待値 |
| `observation.enabled` | adapterが所有する読み取り観測。`save-smoke` は `false` |
| `timeoutSeconds` | 起動から検証完了までの上限。`save-smoke` は30～1800秒、ほかは15～1800秒 |

移動は各軸3マス以内の整数です。プローブ型とソースは adapter ごとに固定し、JSON からコードやメソッドを指定する機能は設けていません。

`actorSource` を省略したケースは `party` として扱います。`guest` はロード済みのゲスト一覧を使います。

共通の `save-smoke` はセーブ読込と通常移動を担当します。個別機能の観測、期待値、作成時の既定値は、Git管理しない `tools/game-test/Scenarios/` のローカル入口が所有します。ローカル入口は `-ScenarioAdapter`、`-ScenarioSource`、`-ScenarioType`、`-ScenarioValidator`、`-ScenarioObservation` をCLI引数で明示します。ケースのadapter名と観測有無がこの指定と一致することを要求し、JSONからソースや型、メソッドを選ぶ機能は設けません。

セーブを使わないローカル入口は `-ScenarioSaveScenario $false` を明示し、操作と期待値を固定したvalidatorで検証します。省略時はセーブを使う既存動作を維持します。

追加のコンパイル参照DLLは `-ScenarioReferencePath` で明示し、原本のパス・SHA-256と保存コピーを実行記録に残します。

移動以外の操作は、ローカル入口で固定した`IActionOperation`を`RunScenario`へ渡します。`Kind`とケースの`operation.kind`を一致させ、操作固有の入力はローカルvalidatorで検証します。`Execute`はメインスレッドで通常のゲーム処理を実行し、前提不足は`BlockedConditionException`、取得できた期待値との不一致は渡された`check`で報告します。`IActionObservation`は操作前後の読み取りに使います。共通入口がセーブの読込、操作区間の記録、画像取得、終了と復元を担当し、各adapterの`Dispose`は成功・失敗の両方で呼ばれます。

ロード後の準備に召喚が必要なケースは、`-SetupPowerName '<所持能力の完全名>'` を指定して作成します。準備用の使用者は `-SetupActorSource`（既定 `party`）と `-SetupActorIndex`（既定 `0`）、召喚位置は `-SetupPositionOffset`（既定 `@(1,0,0)`）、召喚後のゲスト総数は `-SetupExpectedGuestCount`（既定 `1`）で固定します。

JSON の `initialState.setup` は任意です。対応する準備は、所持する主動作・位置指定の召喚能力を1回使う `use-power` です。使用回数、位置の可視性・射程・配置条件、効果成立と消費、ゲスト総数を確認してから `operation` の移動へ進みます。`-SetupPowerName` を省略したケースでは、この準備を実行しません。

`native-summon-icons` は、ネイティブの召喚能力・呪文の画像と定義を読み取る固定プローブです。セーブは指定せず、`operation.kind` を `startup`、`expected.initialized` を `true`、`observation.enabled` を `false` にします。初期設定と配置・復元はほかのケースと共通です。

## 明示配置と実行

ゲームを終了してから実行します。

```powershell
$run = & .\tools\game-test\Invoke-GameTest.ps1 `
    -CasePath $case -BuildDirectory $build -GameExePath "$game\Solasta.exe" `
    -AuthorizedGameDirectory $game -Deploy -Launch
```

入力だけを確認する場合は `-Deploy -Launch` を省き、`-ValidateOnly` を指定します。

表示寸法を調べる場合は`-ObserveDisplay`を追加します。既定は無効です。メインスレッドでScreen、Camera、Canvas、FSRの実寸と設定値を読み、`Runtime-Display.jsonl`へ保存します。

通常の回帰テストは、マウス・キーボードを使わず、非入力のWindowsデスクトップでGPU描画と固定ケースのネイティブアクションを実行します。ホストのデスクトップは切り替えません。デスクトップ・前面アプリ・GPU・フレームの照合は[実行基盤の観測仕様](../runtime-verification/README.md#実行デスクトップと観測)に従い、必須情報の取得失敗やゲームの前面化は `BLOCKED` とします。

Unityの実フォーカス喪失は、OS入力の非占有と別に検証します。`verifyFocusLoss=false`のセーブ回帰は、実フォーカス喪失を検証しません。ケース作成時に`-VerifyFocusLoss`を指定すると、同じ非入力デスクトップ・同じゲームスレッド内に一時ウィンドウを作り、`SetActiveWindow`でそのウィンドウをアクティブにします。実行前後にPID・スレッド・デスクトップ・`UOI_IO`とactive/focus HWNDを照合します。最大3秒で実際のフォーカス喪失通知と`Application.isFocused=false`を確認し、ゲームウィンドウを非アクティブのまま再表示してからアクションへ進みます。描画寸法と非フォーカス中の進行も確認し、条件を満たさなければ`BLOCKED`です。

配置前に変更対象ファイル、UB 設定プロファイル、UMM 設定を退避します。ゲームが終了している間だけ、明示したゲームディレクトリへステージングを配置します。ネイティブ設定はケースから[検証用の保存先](../runtime-verification/README.md#検証用ユーザーデータの隔離)へ複製し、元の `UserPreferences.xml` と `PlatformPreferences.xml` は保持します。起動、一時 UMM mod、ログ収集、ゲーム終了は `tools/runtime-verification/Invoke-RuntimeVerification.ps1` に委ねます。

固定プローブがネイティブの移動アクションを1回実行し、開始位置、到着位置、行動終了・中断状態を判定します。Unity のアクセスと画像取得は既存 harness のメインスレッド上で行います。

全画素のRGBが0の画像は採用せず、ケース期限または5秒まで再取得します。採否、画素数、画像ハッシュを記録し、期限内に採用できなければ `BLOCKED` にします。

## 画面操作の受入確認

`-VisualAcceptance` はWindows VMのゲスト内で実行する画面操作の受入確認専用です。ゲームと操作runnerを同じVM内で動かし、ケース・ゲーム・ビルドもVM内のパスを指定します。ホストからVMの画面をクリックする操作は使いません。VMや追加ソフトの導入は事前に確認します。

VM内の3D描画、画面取得、ゲスト内の入力が利用できることを確認してから、ケース作成時に `-ManualGate -ManualGateTimeoutSeconds 600 -TimeoutSeconds 900` を指定し、VM内で次を実行します。

```powershell
$run = & .\tools\game-test\Invoke-GameTest.ps1 `
    -CasePath $case -BuildDirectory $build -GameExePath "$game\Solasta.exe" `
    -AuthorizedGameDirectory $game -Deploy -Launch -VisualAcceptance
```

ゲームを表示して起動し、ロード後に runtime 証跡へ `GameTest-Ready.json` と `GameTest-Ready.png` を出力して待機します。VMゲスト内で利用できる操作手段で画面取得と入力を確認し、その記録を実行証跡へ保存します。操作を終えたら、同じ runtime 証跡に `continue.json` を作ります。

自動ケースは手動待機なしで進みます。手動待機は1秒からケースの上限まで指定でき、ケース全体の上限には起動・ロード・操作の時間も含まれます。複数の画面操作は同じ待機中にまとめ、確認後に1回だけ続行します。

```powershell
'{"continue":true}' | Set-Content -LiteralPath '<runtime証跡>\continue.json' -Encoding UTF8
```

初期化だけを確認する `startup` adapterは一時harnessを持たないため、`-VisualAcceptance` で実行します。

## Computer Useの操作記録

VMゲスト内でCodex Computer Useを利用できる場合の記録例です。ゲスト内でComputer Use skillの手順に従ってクライアントを初期化し、同じVM内のSolastaのウィンドウを選択します。返却された `sky` と `targetWindow` を使い、ゲスト内のNode REPLで一度だけ記録用セッションを作ります。

```javascript
globalThis.gameUi = await (await import('file:///<VM内のrepo絶対パス>/tools/game-test/ComputerUseSession.mjs')).createComputerUseSession({
  sky, window: targetWindow, evidenceDirectory: '<VM内の今回のartifacts実行証跡>'
});
await gameUi.observe();
```

取得した画像を確認し、次のセルで1操作ずつ実行します。座標は直前の画像から指定します。

```javascript
await gameUi.click({ x: 420, y: 260 });
```

キー操作は `await gameUi.pressKey('Escape');`、スクロールは `await gameUi.scroll({x:420,y:260,scrollY:400});` で記録します。入力後は既定250ミリ秒待ち、新しい画像を1回取得して表示・保存します。各呼び出しの `settleMs` で0～500ミリ秒に調整できます。

`gameUi.directory` に画像と `Operations.jsonl` を保存します。履歴には連番、UTC時刻、入力、直前の画像ID、保存画像のSHA-256を記録します。入力や取得に失敗した場合は `observe()` で画面を確認してから続けます。

## 結果と復元

証跡の保存先は `tools/game-test/artifacts/` です。`-EvidenceRoot` で指定できるのも、この配下に限ります。以下のファイル名・パスは、個々の実行証跡フォルダを基準にしています。

| ファイル | 内容 |
| --- | --- |
| `Result.json` | `acceptanceScope`、`PASS` / `FAIL` / `BLOCKED` / `ERROR`、理由、復元結果 |
| `Build.json`, `Build.log`, `Environment.json` | ビルド識別情報と実行環境 |
| `InputCase.json`, `Case.json`, `Input/` | 受付ケースと実行用の複製入力 |
| `InputAdapterSources/` | 実行に選んだプローブソースと検証スクリプトの原本。`Result.json` にSHA-256を記録 |
| `Operations.json`, `Runtime/*/GameTest-Operations.jsonl` | 配置・起動とゲーム内操作の履歴 |
| `Runtime/*/*.png`, `Runtime/*/Runtime-Player.log` | 画像と当該起動のログ |
| `Runtime/*/GameTest-Smoke.json`, `HarnessResult.json`, `Probe.log` | 期待値・実測値と項目別結果 |
| `Runtime/*/` のadapter固有JSON | 個別プローブが所有する期待値・実測値 |
| `Runtime/*/Runtime-Desktop.json` | ゲーム自身のデスクトップ名、PID、非入力状態 |
| `Runtime/*/Runtime-Desktop.jsonl` | ゲームのmain threadと所有ウィンドウ、入力状態の継続観測 |
| `Runtime/*/Runtime-Foreground.jsonl` | ホスト側の前面HWND・PID・所有thread、入力デスクトップ |
| `Runtime/*/Runtime-Assembly.json` | 実UB Assembly、UMM ModEntry、元DLL・読込キャッシュ・formal buildのSHA-256とMVIDの対応 |
| `Runtime/*/Runtime-Display.jsonl` | `-ObserveDisplay`で取得した表示寸法、FSR、Canvas、Camera |
| `Runtime/*/Runtime-Execution.jsonl`, `GameTest-Loading.jsonl` | メインスレッドのbatchmode、GPU、フォーカス、時間倍率、フレーム進行、ロード状態 |
| `Runtime/*/GameTest-Captures.jsonl` | 画像の採否、画素数、SHA-256 |
| `Transaction.json`, `Restore.log` | 配置前の状態と復元記録 |
| `Backup/`, `BackupRelease.json` | 復元用の退避と、復元確認後の解放記録 |

`Result.json`の`acceptanceScope`は、実行した検証の範囲を示します。

| `acceptanceScope` | 対象 |
| --- | --- |
| `native-non-occupying-runner` | `verifyFocusLoss=false`のセーブ回帰。OS入力の非占有、通常アクション、必要な描画と復元 |
| `unity-focus-loss` | `verifyFocusLoss=true`のセーブ回帰。通常回帰の条件に加え、実測した非フォーカス中のアクション進行 |
| `native-observation` | セーブ回帰を行わない、固定プローブによる観測 |
| `visual-observation` | `-VisualAcceptance`による実行。通常UI入力の合否は操作と画面の証跡から別に判定 |

scopeは判定結果や必須条件を変更しません。判定対象を次の項目へ分け、実行前に固定した必須条件を、結果を見てから任意条件へ変えません。

| 項目 | 観測と判定 |
| --- | --- |
| 実行成果物 | formal buildのDLL SHA-256と、実ゲームプロセスが読み込んだUBの対応。通常回帰で必須 |
| OS入力の非占有 | ホストとゲームの観測を分け、ゲームPID、ウィンドウ、その所有スレッド、デスクトップ、入力状態を対応付ける。通常回帰で必須。API失敗や不明値は非入力の根拠にしない |
| 通常アクション | ゲーム内command serviceの完了コールバック、終了状態、位置、メインスレッドのフレーム進行。保存済みゲームの回帰で必須 |
| Unityの実フォーカス喪失 | 実測した `Application.isFocused` と遷移、非フォーカス中の進行。OS前面アプリの観測と分離する。`verifyFocusLoss=true` のケースで必須 |
| 通常UI入力 | 実際の画面・入力経路での操作履歴。ゲーム内command、selection service、EventSystem callbackの直接呼出しで迂回した操作は未検証 |
| 描画 | GPU情報、フレーム進行、非黒画素を持つPNG原本とハッシュ。画像を要求する通常回帰で必須。レイアウトの目視判定は別に記録 |
| 音声 | ネイティブイベントの受理と、出音・音質の確認を分離する。イベント戻り値だけで実際の出音を合格にしない |
| 復元 | 導入前ファイル、設定、セーブ原本のバイト列・更新時刻・属性。配置を伴う実行で必須 |

`PASS` は固定した必須項目がすべて取得でき、期待結果と復元が成立したケースです。`FAIL` は取得した実測値と期待値の不一致、`BLOCKED` は前提不足・時間切れ・必須の観測不足・検証のみの実行、`ERROR` はビルド・コンパイル・実行基盤・復元のエラーです。必須項目の不明値を `false` や成功値へ変換しません。

`backgroundActionProof` はネイティブアクション、フレーム進行、Unityフォーカスを項目別に記録します。`unityFocusTransitionVerified` は、実際のフォーカス獲得状態と喪失を両方観測した場合に `true` になります。メニュー・ボタンの通常操作は `-VisualAcceptance` の結果として分けます。

ケースの `PASS` は未検証項目の合格を表しません。依頼全体の受入で必要な項目が `BLOCKED` または未検証なら、受入全体も未完了です。過去の結果ファイルは変更せず、再監査・再検証の結果を別の証跡へ保存します。

資産制作の検査は[asset-authoring](../asset-authoring/README.md)に従います。Blender工程内の`passed`、互換Unityでの資産ビルド・再読込、実ゲームでの資産ロード・表示・再生は、それぞれの工程で判定します。

終了・例外・タイムアウト後は、導入済みmodと設定を元のバイト列、更新時刻、属性へ戻します。Unityの表示用レジストリ値も型と値を退避・復元します。UMMの既存DLLキャッシュと付随するPDBは保持し、この実行で作られたものは配置元のハッシュと照合して撤去します。セーブ原本もハッシュ、更新時刻、属性を照合します。復元時にゲームが起動している場合は処理を止め、証跡と配置トランザクションを残します。

復元を確認すると `Backup/` を自動解放し、`BackupRelease.json` に件数・容量・完了状態を保存します。結果、ログ、画像、入力の複製は保持します。

同じ製品ビルドのケースは保存済みの `$build` を再利用します。配置・復元では期待SHA-256と既存ファイルが一致するときだけコピーを省き、バックアップと最終照合は全対象に実施します。`Operations.json` の工程時刻で、事前検証・バックアップ・起動から検証完了・復元の所要時間を確認できます。

PowerShell 自体の強制終了などで復元が残った場合は、ゲームを終了して次を実行します。

```powershell
& .\tools\game-test\Restore-GameTest.ps1 `
    -RunDirectory '<実行証跡>' -AuthorizedGameDirectory $game
```

復元は再実行できます。インストール先の `Mods/.UBGameTestTransaction.json` が残っている間、新しい配置を拒否します。検証元のビルドと結果は、ゲームへ復元した版とそれぞれのハッシュで区別できます。

移動した証跡の元のJSONは変更しません。`tools/game-test/artifacts/Migration.json` の `mappings` に `originalRoot` と `currentRoot` を記録すると、復元時の旧 `RunDirectory`、所有マーカー、ケースとビルドの入力パスを現在の配置へ解決します。移行先は `tools/game-test/artifacts/` 配下に限定し、更新時刻はUTC ticksで照合します。

## 表示証跡のレビュー

`Invoke-GameTest`は復元後、`PresentationEvidence*.json`に記録された画像・判定・期待値・実測値・出典を`PresentationReview/Review.html`と`Summary.json`へ集約します。判定基準は各検証adapterが所有し、manifestの`FAIL`を実行結果へ反映します。native検証の結果は`Result.json`の`nativeVerificationStatus`に保持します。

既存証跡には `& .\tools\game-test\Review-PresentationEvidence.ps1 -EvidencePath '<Runtime>' -OutputDirectory '<review>'` を使います。manifestファイルも直接指定できます。`-ReferenceImagePath '<画像>'`で参照画像を並べ、判定と画像を照合します。

## 証跡の容量管理

修正の確認・比較に必要なログ、画像、入力は保持します。不要になった復元済みバックアップは `Clear-GameTestArtifacts.ps1` で整理します。既定は削除を行わないプレビューで、対象と容量を保存先の `Storage-Cleanup-*.json` に記録します。内容を確認してから `-Apply` を付けます。

```powershell
$evidence = '.\tools\game-test\artifacts'
& .\tools\game-test\Clear-GameTestArtifacts.ps1 -ArtifactRoot $evidence
& .\tools\game-test\Clear-GameTestArtifacts.ps1 -ArtifactRoot $evidence -Apply
```

`-PruneSuperseded` を追加すると、保存先の直下にある古い実行フォルダと検証ビルドも対象にします。ケースごとと検証ビルドで、それぞれ完了日時が新しい2件を保持します。保持件数は `-KeepLatestPerCase` で変更できます。検証ごとに保存先を分けた場合は、実行フォルダが並ぶ場所を `-ArtifactRoot` に指定します。

検証用の設定・出力を保存した `artifacts/.native/` は、対応する実行証跡と一緒に保持します。古い実行を整理するときだけ、`Native-UserDataLocation.json` と両所有マーカーが一致する直下の所有フォルダも対象にします。不一致やreparse pointがある場合は両方を保持します。作業用保存先を `-PreserveDirectory` に指定した場合も、対応する実行証跡を保持します。

比較用などの証跡は `-PreserveDirectory` に配列で指定します。指定したフォルダとその配下はバックアップも含めて保持します。`tools/game-test/Cases/`・`tools/game-test/Scenarios/` から名前を参照される実行フォルダ、復元未完了の実行、未解決の配置マーカーがあるゲームの証跡、未知のバックアップファイルも保持します。対象は `tools/game-test/artifacts/` 配下に限り、reparse pointを含むツリーの整理は拒否します。

## Gitの追跡範囲

`tools/game-test/`の実行スクリプト、共通実装、共通プローブとREADMEを追跡します。`tools/game-test/Scenarios/` の個別コード、`tools/game-test/Cases/` のセーブ・固定設定、`tools/game-test/artifacts/` のログ・画像・レポート、一時ファイルは追跡しません。依存する実行基盤の追跡範囲は[Runtime verification](../runtime-verification/README.md#gitの追跡範囲)に従います。
