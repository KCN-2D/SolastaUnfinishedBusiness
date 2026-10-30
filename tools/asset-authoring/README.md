# モデル・骨格アニメーション制作ツール

BlenderのPython APIで生成、書き出し、再読み込み検証、PNGプレビュー出力を実行し、必要な資産をUnity形式へ変換します。パス表記は[ツール共通の規約](../README.md#パス表記)に従います。

## 必要条件

- Windows、PowerShell
- Blender **4.5.9 LTS**。既定の配置先は`tools/asset-authoring/workspace/Tools/blender-4.5.9-windows-x64/blender.exe`
- Unity資産をビルドする場合はUnity Editor **2019.4.37f1**、Windows 64bit用ビルド機能

別の配置先は`-BlenderPath`または`-UnityPath`で指定します。PythonはBlender同梱版を使います。

## 実行

リポジトリのルートから実行します。

```powershell
.\tools\asset-authoring\Invoke-AssetAuthoring.ps1
```

既定では静的モデルと1秒の骨格アニメーションを生成し、OBJ / FBXを別プロセスで再読み込みして検証します。

| 引数 | 用途 |
| --- | --- |
| `-SourcePath` | 追加で読み込むスクリプト・設定ファイル。原本とハッシュを保存し、各工程の前後に変更を検出 |
| `-DependencyPath` | 出力が参照する入力・既存成果物。整理時の保持対象 |
| `-PipelinePath` | 実行するPythonスクリプト。既定は`tools/asset-authoring/blender_pipeline.py` |
| `-QualityProfilePath` | モデル側の検証要件JSON。指定時は最後に共通の`validate-quality`工程を実行 |
| `-Stages` | 順番に実行する工程。既定は`generate`、`verify-static`、`verify-rig` |
| `-ResultName` | 最新の成功結果を`last-<名前>-success.json`に保存。省略時は`last-success.json` |
| `-BlenderPath` | Blender実行ファイル |
| `-Threads` | CPUスレッド数。既定2、範囲1～64 |
| `-CyclesBackend` | 明示して使うCycles GPUバックエンド。`CUDA`、`OPTIX`、`HIP`、`METAL`、`ONEAPI` |
| `-CyclesDeviceId` | 対象バックエンドから実際に列挙したデバイスID。`-CyclesBackend`と同時に指定 |
| `-OutputRoot` | 出力の親フォルダ。`tools/asset-authoring/workspace/`配下を指定 |

GPUを指定すると、そのBlenderプロセスだけで対象デバイスを使用し、各工程の選択結果を`reports/<工程名>-cycles-device.json`へ保存します。ユーザー設定は保存しません。指定したデバイスが利用できない場合は停止します。省略時は各制作処理の描画設定を使います。

個別モデルの生成処理はモデル側に置き、`-PipelinePath`と`-Stages`を指定して実行します。各工程は`--stage <工程名> --output <出力先>`を受け取り、`reports/<工程名>.json`へ`stage`と`status`を出力します。成功時の`status`は`passed`です。

PowerShellの実行ポリシーで停止した場合は、次の形式で起動します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\asset-authoring\Invoke-AssetAuthoring.ps1
```

## モデルごとの検証要件

生成スクリプトと同じモデル側のフォルダに検証要件JSONを置き、`-QualityProfilePath`で指定します。`required_stages`の工程を順番どおり実行し、書き出したOBJの寸法・材質名・三角形数と、指定した全プレビューを別プロセスで検査します。

```json
{
  "schema_version": 1,
  "required_stages": ["generate", "verify"],
  "obj_assets": [{
    "path": "static/Model/Model.obj",
    "bounds_metres_y_up": {"min": [-0.1, 0, -0.1], "max": [0.1, 0.3, 0.1], "tolerance_metres": 0.0002},
    "materials": ["Main"], "max_triangles": 1000
  }],
  "previews": [{
    "path": "previews/model_front.png", "view": "front", "width": 384, "height": 384,
    "engine": "BLENDER_WORKBENCH", "camera_direction": [0, -1, 0]
  }]
}
```

寸法はOBJのY上座標、カメラ方向はBlender上で被写体からカメラへ向かう方向です。カメラの許容角度は既定0.5度で、プレビューごとに`angle_tolerance_degrees`を指定できます。`obj_assets`と`previews`は、必要な検証をそれぞれ列挙します。

共通の`render_preview`と`render_views`はPNGに対応する`.capture.json`へ実際のカメラ・描画エンジン・画像ハッシュを保存します。独自の描画処理を使う場合は、描画直後に共通関数`preview_receipt(path, view="front")`を呼びます。

```powershell
.\tools\asset-authoring\Invoke-AssetAuthoring.ps1 `
    -PipelinePath .\tools\blender-models\Model\generate.py -Stages generate,verify `
    -QualityProfilePath .\tools\blender-models\Model\quality.json
```

## 出力と検証

出力先は既定で`tools/asset-authoring/workspace/Output/<日時>-<識別子>/`です。以下の出力ファイル・フォルダと、工程が出力する`reports/<工程名>.json`は、この実行フォルダが基準です。終了コード0と`run.json`の`status=passed`で全工程の成功を確認します。

`validation_scope=blender_quality_profile`では要件JSONの全項目、`declared_pipeline_stages`では指定した工程の結果を確認しています。要件JSONを指定した場合は`quality.status=passed`も確認します。

```powershell
$result = Get-Content .\tools\asset-authoring\workspace\Output\last-success.json -Raw | ConvertFrom-Json
Invoke-Item $result.output
```

| 出力 | 内容 |
| --- | --- |
| `sources/` | 編集用の`.blend` |
| `static/` / `rig/` | 既定サンプルのOBJ / MTL、骨格付きFBX |
| `previews/` | 再読み込み後の正面・側面・上面・斜視、アニメーション代表フレーム、比較画像、描画情報 |
| `reports/` | 工程別の検査結果と測定値 |
| `InputSources/` | 実行したスクリプトの原本 |
| `failures/` | 検査失敗時のBlenderシーン |
| `run.json` | 入力・出力のハッシュ、工程の時間、測定値、成否 |

共通検証処理は`tools/asset-authoring/blender_pipeline.py`にあります。保存した`.blend`を開き直し、OBJ / FBXの座標、bounds、三角形の接続・向き、面の材質割当・基本色、面頂点のUV・法線を照合します。骨格サンプルは3本の骨、頂点ごとのウェイト、25フレーム全ての変形行列とスキンを比較します。書き出し時の三角形分割を揃えるため、サンプルの原本にはTriangulate modifierを設定しています。

| 比較項目 | 許容誤差 |
| --- | --- |
| 座標・bounds | 0.0002 m |
| UV | 0.00001 |
| 単位法線の距離 | 0.0001 |
| 材質の基本色RGBA | 0.00001 |
| ウェイト | 0.00001 |
| 骨の変形行列要素 | 0.0002 |

工程のログは`reports/<工程名>.log`に保存します。失敗した実行も入力、書き出し資産、測定値、シーンを保持し、最新成功結果の参照は更新しません。

共通検証の契約テストは次のコマンドで実行します。

```powershell
.\tools\asset-authoring\Invoke-AssetAuthoring.ps1 `
    -PipelinePath .\tools\asset-authoring\tests\quality_contracts.py -Stages verify-contract -ResultName quality-contracts
```

採用するOBJ / MTL / テクスチャを`SolastaUnfinishedBusiness/BlenderModels/`へ反映した後、[正式ビルド](../CustomBuildLocal/README.md)でパッケージを作ります。

## UBのOBJ形式

- `SolastaUnfinishedBusiness/BlenderModels/<Name>/<Name>.obj`と同じフォルダに、同名の`<Name>.mtl`を配置します。
- 面は三角形、インデックスは正の1始まりの`v/vt/vn`にします。法線は必須です。
- OBJはY上、+Z前です。1数値を1 Unity local unitとして扱います。
- モデル名・材質名・テクスチャの相対パスに空白を含めません。
- MTLの`map_Kd`は色、`map_Bump`は法線画像として読み込まれます。
- 独自shaderが必要な場合は、互換Unityで作成した`<Name>.materials.bundle`をOBJと同じフォルダへ配置します。MTLと同名のMaterialを複製し、MTLの色・透明度・テクスチャ等を反映します。shaderの描画パス・ブレンド設定はMaterial側で指定します。
- 三角形ごとに3頂点へ展開されるため、展開後の頂点数を65,536未満にします。

読み込み処理は`SolastaUnfinishedBusiness/CustomUI/CustomModels.cs`にあります。

## 骨格付きFBXのUnity資産への変換

UBのOBJ経路は静的メッシュ用です。骨格付きFBXは、`tools/asset-authoring/UnityAssetBuild/`のテンプレートからPrefab、AnimatorController、AssetBundleを作成します。

```powershell
$result = Get-Content .\tools\asset-authoring\workspace\Output\last-success.json -Raw | ConvertFrom-Json
.\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx')
```

作業先は`tools/asset-authoring/workspace/UnityWorkspace/<日時>-<識別子>/Project/`です。実行フォルダ直下の`Editor.log`と、`Project/BuildOutput/unity-report.json`でビルド・再読み込み結果を確認します。

`-ColorSpace`で出力先の色空間を`Linear`または`Gamma`に指定します。既定は`Linear`です。

`-ValidateOnly`を付けると、入力の宣言と参照ファイルの検査だけを実行します。共通の入力検査は`tests/Test-UnityAssetBuildContracts.ps1 -InputValidationOnly`で確認できます。

認証済みEditorを使う場合は、作業用Projectに`-GuiProjectPath`を指定します。`-StartBackgroundEditor`を付けると、Unity CLIから非入力デスクトップへEditorを起動し、ジョブ終了後にEditorとデスクトップを解放します。PowerShell 7とUnity CLIが必要です。

```powershell
pwsh -NoProfile -File .\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx') `
    -GuiProjectPath '<作業用Projectのパス>' -InstallGuiWorker -StartBackgroundEditor
```

Unity CLIは既定で`%LOCALAPPDATA%\Unity\bin\unity.exe`を使います。別の配置先は`-UnityCliPath`で指定します。既に開いているEditorを使う場合は`-StartBackgroundEditor`を外します。初回とworker更新時は`-InstallGuiWorker`で拡張を配置し、既存Editorでは`Assets > Refresh`で読み込みます。

workerの結果にはコンパイル済みコードの識別子を記録し、呼び出し側で依頼時の期待値と照合します。識別子の欠落・不一致は失敗とし、独自workerにも同じ照合を設けます。

指定したUnity Projectを基準に、FBXと生成アセットは`Assets/AuthoringJobs/<ジョブ名>/`、ジョブ・結果・AssetBundleは`.asset-authoring-jobs/<ジョブ名>/`へ保存します。Editorのメインスレッドでインポート、ビルド、再読み込みを行い、開いているシーンは変更しません。非占有起動のPID・開始時刻・デスクトップ・入力状態・正常終了は`.asset-authoring-jobs/BackgroundSessions/<識別子>/session.json`へ記録します。既存Editorを使った場合は終了せず、起動したEditorだけを終了します。失敗したジョブも保持します。

非占有起動の契約テストでは、既存workerの継続、2回の制作・再読み込み、入力不正時の後始末を検証します。

```powershell
pwsh -NoProfile -File .\tools\asset-authoring\tests\Test-UnityAssetBuildContracts.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -GuiProjectPath '<作業用Projectのパス>' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx') `
    -BackgroundEditorOnly -InstallGuiWorker
```

GUI経路の契約テストは、現在のworkerを読み込んだProjectを指定して実行します。

```powershell
.\tools\asset-authoring\tests\Test-UnityAssetBuildContracts.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx') `
    -GuiProjectPath '<作業用Projectのパス>'
```

`-CliTimeoutOnly`は起動CLIのタイムアウト後の終了処理、`-CallerStopOnly`は起動待ちの中断・呼び出し元終了後の停止を検証します。どちらもEditorを起動せず、非入力デスクトップ上でテスト用CLIを使います。`-FbxPath`は不要です。

Pythonでレシピを生成する場合は、[asset_names.py](asset_names.py)の`ResourceName(key, label, detail, suffix)`と`resource_names`を使い、元資産名を`label`、同名を区別するメッシュ・材質名を`detail`、LODや用途を`suffix`に指定します。生成前に`assert_path_budget`で出力先の絶対パスと処理ごとの文字数上限を照合し、Unity入力は`.meta`を含む長さで確認します。

材質・シェーダーを同梱する場合は、`-FbxPath`の代わりに`-MaterialRecipePath`を指定します。

```powershell
.\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -MaterialRecipePath '<材質レシピ.json>' -GuiProjectPath '<作業用Projectのパス>'
```

レシピは`schemaVersion=1`、`bundleName`、`assets`、`materials`を持つJSONです。`assets`にはレシピからの相対`path`と小文字の`sha256`を記録します。入力は`.shader`、`.cginc`、`.hlsl`、`.png`、`.jpg`、`.jpeg`、`.tga`に対応します。

各材質は`name`、宣言したShaderの相対`shader`、検証する`requiredPasses`を指定します。`renderQueue`と`properties`は任意です。プロパティは`name`と`kind`に加え、`float`なら`value`、`color`なら4成分の`rgba`、`texture`なら宣言した入力の相対`texture`を指定します。ビルド後はMaterial、Shader、必要な描画パス、設定値を再読み込みで照合します。レシピとShaderは各モデルの制作ファイルと一緒に管理します。

契約テストにも`-MaterialRecipePath`を渡すと、材質の再読み込み、描画パス不足、プロパティ型の不一致、入力ハッシュ不一致を確認します。同名のShaderや同名・同寸法のTextureについても、宣言した資産パスに対応するbundle内参照と照合します。

材質だけを再検証する場合は、契約テストに`-MaterialsOnly -MaterialRecipePath '<材質レシピ.json>'`を指定し、`-FbxPath`を省略します。workerを更新する場合は`-InstallGuiWorker`を追加します。

頂点チャンネルを保ったMesh AssetBundleを作成するには、`-MeshRecipePath`を指定します。

```powershell
pwsh -NoProfile -File .\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -MeshRecipePath '<メッシュレシピ.json>' -GuiProjectPath '<作業用Projectのパス>' `
    -InstallGuiWorker -StartBackgroundEditor
```

レシピは`schemaVersion=1`、`bundleName`、`meshes`を持ちます。各meshは一意な`name`、`vertices`、`normals`、`tangents`、`uvChannels`、`subMeshes`、`bounds`、`indexFormat`を指定します。`colors`は任意のRGBA配列です。頂点・法線は3成分、接線・色は4成分の平坦な数値配列で、使わない法線・接線は空配列にします。UVは`channel`（0〜7）、`dimension`（2・3・4）、`values`を指定します。submeshは`topology=Triangles`と`indices`、boundsは3成分ずつの`center`と`size`、indexFormatは`UInt16`または`UInt32`です。

大きいメッシュは、数値データを別ファイルへ出力できます。同じディレクトリに新しいレシピ名を指定すると、Textureの相対パスを引き継げます。

```powershell
python .\tools\asset-authoring\UnityAssetBuild\mesh_recipe_codec.py `
    --input '<ディレクトリ>/mesh-recipe.json' --output '<ディレクトリ>/mesh-binary.json'
```

出力レシピは`meshes`の代わりに`numericPayload`の形式名・相対パス・SHA256・byteLengthを持ちます。JSONと数値ファイルはそれぞれ64MiB以下です。数値ファイルはlittle-endianのbinary32/int32とUTF8名を使い、頂点・骨格・表情の物理順序とビット列を保持します。

骨格データは任意の`skin`に、`bindPoses`（骨ごとにUnityの`Matrix4x4[i]`順、列優先の16成分）、`boneWeights`（頂点ごとに降順の4成分、合計1）、`boneIndices`（頂点ごとに4成分、bindPoses内の骨番号）を指定します。`blendShapes`は物理index順の配列で、各要素に`name`と`frames`を指定します。同名のチャンネルも分離して保持し、名前で検索した際の最初のindexを照合します。各frameは昇順の`weight`と、頂点ごとに3成分の変位配列`vertices`・`normals`・`tangents`を持ちます。

同じbundleへTexture2Dを含める場合は、レシピ直下に任意の`textures`配列を指定します。各要素は`name`、レシピからの相対`path`、小文字の`sha256`、真偽値の`normalMap`・`sRGB`・`mipmaps`を持ちます。PNG / JPG / JPEG / TGAに対応し、`name`は拡張子を除いたファイル名と一致させます。名前とパスは一意にします。たとえば法線画像は`{"name":"SurfaceNormal","path":"Textures/SurfaceNormal.png","sha256":"<64桁のハッシュ>","normalMap":true,"sRGB":false,"mipmaps":true}`と宣言します。

`normalMap=true`の画像は、接線空間のX・Y・ZをRGBの0〜1へ格納します。RGBA8の法線は共通関数`normal_map_rgb(rgba, source_encoding)`で変換でき、`source_encoding`に`RGB`・`RG`・`AG`を指定します。

テクスチャのみのbundleは`meshes=[]`と1件以上の`textures`で作成します。

Blenderで既存の頂点法線を設定する場合は、共通関数`set_mesh_custom_normals(mesh, normals)`を使います。頂点ごとの3成分配列を渡すと、スムーズ表示を設定してから法線を適用します。

RGBAの各チャンネルに材質データを保存するPNGは、共通関数`write_packed_rgba(path, width, height, rgba)`で出力できます。`rgba`には下の行から並べたRGBA8の`bytes`を渡します。色空間の変換は呼び出し側で行い、関数は渡された値を保持して保存します。

`normalMap=true`はUnityのNormalMapとして取り込み、ハイト画像からの変換を無効にします。法線画像の`sRGB`は`false`にします。取込時は通常圧縮、Crunch無効、宣言したmipmap設定を使い、圧縮データの照合のためRead/Writeを有効にします。作成時、保存したasset、bundle再読み込み後の取込設定、名称、寸法、graphicsFormat、mip数、圧縮済み生データのハッシュを照合します。

非有限値、不正な配列長・indices・bounds、重複名・UV、骨番号・ウェイト・frame順の不整合を拒否します。作成直後、保存したasset、AssetBundle再読み込み後に全チャンネルのfloat32ビット列、UV次元、indices、submesh、bounds、名称、骨格・blendshape全frameを照合します。契約テストに`-MeshesOnly -MeshRecipePath '<メッシュレシピ.json>'`を渡すと、指定レシピと共通の骨格付き複数frame fixtureのビルド、不正入力の拒否を確認します。

## 作業データの整理

通常の生成では、入力依存とジョブの所有範囲を登録し、終了時に成果物のハッシュを保存します。Unityのローカル生成処理は`Register-AssetAuthoringJob`と`Complete-AssetAuthoringJob`で、ジョブの成果物、Unityへの取込用コピー、セッションを登録します。

```powershell
& .\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 -Cleanup `
  -GuiProjectPath '<Projectのパス>' -ReferencePath '<参照JSON>' `
  -MaxAgeDays 7 -MaxRetainedBytes 20000000000

& .\tools\asset-authoring\Invoke-AssetAuthoring.ps1 -Cleanup `
  -OutputRoot '<出力先>' -ReferencePath '<参照JSON>' `
  -MaxAgeDays 7 -MaxRetainedBytes 20000000000
```

完了したジョブの成果物とUnityへの取込用コピーは別に整理します。成果物の参照だけでは取込用コピーを保持しません。取込用コピーを確認・編集する場合は、その`Assets`フォルダーを参照または保持対象に指定します。

既定は整理候補の確認で、`-Apply`で削除します。`-KeepLatest`は既定2件、0で件数による保持を行いません。期限・容量の0は制限なしです。参照中・明示保持・未完了・稼働中のデータを保持し、古いBlender出力の成功ポインターも対象と一緒に整理します。参照JSONは`{"schemaVersion":1,"paths":["<保持する入力>"]}`の形式で、相対パスはJSONの場所を基準にします。

## ゲーム内検証

サンプル資産はゲーム内未検証です。組み込む際は[実ゲーム回帰テスト](../game-test/README.md)の手順で、次を確認します。

- 寸法、向き、裏面カリング、shader、影、材質、装備位置
- 関節・Animatorの対応、動作の遷移、攻撃・詠唱・移動とエフェクトのタイミング
- 地形との接触、当たり判定、ポートレート
- セーブ読み込み後の復元、マルチプレイ同期
- AssetBundleの読み込み・再生とリソース解放
