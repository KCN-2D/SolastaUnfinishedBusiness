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

## 出力と検証

出力先は既定で`tools\asset-authoring\workspace\Output\<日時>-<識別子>`です。終了コード0と`run.json`の`status=passed`で全工程の成功を確認します。

```powershell
$result = Get-Content .\tools\asset-authoring\workspace\Output\last-success.json -Raw | ConvertFrom-Json
Invoke-Item $result.output
```

| 出力 | 内容 |
| --- | --- |
| `sources\` | 編集用の`.blend` |
| `static\` / `rig\` | 既定サンプルのOBJ / MTL、骨格付きFBX |
| `previews\` | 再読み込み後の正面・側面・上面・斜視、アニメーション代表フレーム、比較画像 |
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

採用するOBJ / MTL / テクスチャを`SolastaUnfinishedBusiness\BlenderModels`へ反映した後、`tools\CustomBuildLocal\BuildCustomLocalPackage.cmd`でパッケージを作ります。

## UBのOBJ形式

- `BlenderModels/<Name>/<Name>.obj`と同名の`<Name>.mtl`を配置します。
- 面は三角形、インデックスは正の1始まりの`v/vt/vn`にします。法線は必須です。
- OBJはY上、+Z前です。1数値を1 Unity local unitとして扱います。
- モデル名・材質名・テクスチャの相対パスに空白を含めません。
- MTLの`map_Kd`は色、`map_Bump`は法線画像として読み込まれます。
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

## ゲーム内検証

サンプル資産はゲーム内未検証です。組み込む際は次を確認します。

- 寸法、向き、裏面カリング、shader、影、材質、装備位置
- 関節・Animatorの対応、動作の遷移、攻撃・詠唱・移動とエフェクトのタイミング
- 地形との接触、当たり判定、ポートレート
- セーブ読み込み後の復元、マルチプレイ同期
- AssetBundleの読み込み・再生とリソース解放
