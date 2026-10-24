# モデル・骨格アニメーション制作ツール

BlenderのPython APIで生成、書き出し、再読み込み検証、PNGプレビュー出力を実行します。

## 必要条件

- Windows、PowerShell
- Blender **4.5.9 LTS**。既定の配置先は`tools\asset-authoring\workspace\Tools\blender-4.5.9-windows-x64\blender.exe`
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
| `-PipelinePath` | 実行するPythonスクリプト。既定は`blender_pipeline.py` |
| `-QualityProfilePath` | モデル側の検証要件JSON。指定時は最後に共通の`validate-quality`工程を実行 |
| `-Stages` | 順番に実行する工程。既定は`generate`、`verify-static`、`verify-rig` |
| `-ResultName` | 最新の成功結果を`last-<名前>-success.json`に保存。省略時は`last-success.json` |
| `-BlenderPath` | Blender実行ファイル |
| `-Threads` | CPUスレッド数。既定2、範囲1～64 |
| `-OutputRoot` | 出力の親フォルダ。`tools\asset-authoring\workspace`配下を指定 |

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

出力先は既定で`tools\asset-authoring\workspace\Output\<日時>-<識別子>`です。終了コード0と`run.json`の`status=passed`で全工程の成功を確認します。

`validation_scope=blender_quality_profile`では要件JSONの全項目、`declared_pipeline_stages`では指定した工程の結果を確認しています。要件JSONを指定した場合は`quality.status=passed`も確認します。

```powershell
$result = Get-Content .\tools\asset-authoring\workspace\Output\last-success.json -Raw | ConvertFrom-Json
Invoke-Item $result.output
```

| 出力 | 内容 |
| --- | --- |
| `sources\` | 編集用の`.blend` |
| `static\` / `rig\` | 既定サンプルのOBJ / MTL、骨格付きFBX |
| `previews\` | 再読み込み後の正面・側面・上面・斜視、アニメーション代表フレーム、比較画像、描画情報 |
| `reports\` | 工程別の検査結果と測定値 |
| `InputSources\` | 実行したスクリプトの原本 |
| `failures\` | 検査失敗時のBlenderシーン |
| `run.json` | 入力・出力のハッシュ、工程の時間、測定値、成否 |

共通検証処理は`blender_pipeline.py`にあります。保存した`.blend`を開き直し、OBJ / FBXの座標、bounds、三角形の接続・向き、面の材質割当・基本色、面頂点のUV・法線を照合します。骨格サンプルは3本の骨、頂点ごとのウェイト、25フレーム全ての変形行列とスキンを比較します。書き出し時の三角形分割を揃えるため、サンプルの原本にはTriangulate modifierを設定しています。

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

採用するOBJ / MTL / テクスチャを`SolastaUnfinishedBusiness\BlenderModels`へ反映した後、`tools\CustomBuildLocal\BuildCustomLocalPackage.cmd`でパッケージを作ります。

## UBのOBJ形式

- `BlenderModels/<Name>/<Name>.obj`と同名の`<Name>.mtl`を配置します。
- 面は三角形、インデックスは正の1始まりの`v/vt/vn`にします。法線は必須です。
- OBJはY上、+Z前です。1数値を1 Unity local unitとして扱います。
- モデル名・材質名・テクスチャの相対パスに空白を含めません。
- MTLの`map_Kd`は色、`map_Bump`は法線画像として読み込まれます。
- 独自shaderが必要な場合は、互換Unityで作成した`<Name>.materials.bundle`をOBJと同じフォルダへ配置します。MTLと同名のMaterialを複製し、MTLの色・透明度・テクスチャ等を反映します。shaderの描画パス・ブレンド設定はMaterial側で指定します。
- 三角形ごとに3頂点へ展開されるため、展開後の頂点数を65,536未満にします。

読み込み処理は`SolastaUnfinishedBusiness/CustomUI/CustomModels.cs`にあります。

## 骨格付きFBXのUnity資産への変換

UBのOBJ経路は静的メッシュ用です。骨格付きFBXは、分離した`UnityAssetBuild`テンプレートからPrefab、AnimatorController、AssetBundleを作成します。

```powershell
$result = Get-Content .\tools\asset-authoring\workspace\Output\last-success.json -Raw | ConvertFrom-Json
.\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx')
```

作業先は`tools\asset-authoring\workspace\UnityWorkspace\<日時>-<識別子>\Project`です。同じ実行フォルダの`Editor.log`と、`BuildOutput\unity-report.json`でビルド・再読み込み結果を確認します。

Hubで認証したEditorを継続して使う場合は、この作業用Projectを開き、同じ入口に`-GuiProjectPath`を指定します。初回は`-InstallGuiWorker`でEditor拡張を配置し、Editorの`Assets > Refresh`を選びます。

```powershell
.\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx') `
    -GuiProjectPath '<作業用Projectのパス>' -InstallGuiWorker
```

以後は`-InstallGuiWorker`を省略して繰り返します。workerの更新時は再び指定すると、読み込み済みのworkerが更新を取り込みます。FBXと生成アセットは`Assets\AuthoringJobs\<識別子>`、ジョブ・結果・AssetBundleは`.asset-authoring-jobs\<識別子>`に分けて保存します。Editorのメインスレッドでインポート、ビルド、再読み込みを行い、開いているシーンは変更しません。生成したアセットだけを保存し、Editorは終了しません。タイムアウトしたジョブは保持するため、再実行前にその結果を確認します。

GUI経路の契約テストは、現在のworkerを読み込んだProjectを指定して実行します。

```powershell
.\tools\asset-authoring\tests\Test-UnityAssetBuildContracts.ps1 `
    -UnityPath 'C:\Tools\Unity\2019.4.37f1\Editor\Unity.exe' `
    -FbxPath (Join-Path $result.output 'rig\UBSampleRig.fbx') `
    -GuiProjectPath '<作業用Projectのパス>'
```

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

## ゲーム内検証

サンプル資産はゲーム内未検証です。組み込む際は次を確認します。

- 寸法、向き、裏面カリング、shader、影、材質、装備位置
- 関節・Animatorの対応、動作の遷移、攻撃・詠唱・移動とエフェクトのタイミング
- 地形との接触、当たり判定、ポートレート
- セーブ読み込み後の復元、マルチプレイ同期
- AssetBundleの読み込み・再生とリソース解放
