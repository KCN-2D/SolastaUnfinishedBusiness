# 髪・ヒゲの制作

髪とヒゲには共通のshaderを使い、ゲームの色選択、透過、dissolve、金具のmaskを引き継ぎます。`materials.json`にshaderとincludeを登録し、`SourceGroomProgram.cginc`で表面の描画を処理します。
制作と作業データの管理は[asset-authoring](../../asset-authoring/README.md)を参照してください。

髪の滑らかな法線・接線は頂点データと物理変形の接続へ反映します。金具の領域は元のフレームへ戻す座標を使います。`_SupportInVertexFrame`でこの形式を識別し、旧形式の資源も同じshaderで読み取ります。実行時のbundleはLZ4で保存します。

UV0.xyは元の画像、UV2は毛束の座標と役割、UV4は制作時の毛流れを持ちます。`_FlowInVertexFrame=2`の資源は、金具のフレーム復元と毛流れの各2成分をUV0.zwへ12bitずつ格納し、頂点shaderで復元します。UV1の既存データを保ち、UV3は実行時のGPU曲面変形に使います。

## 材質の生成

shader/includeを変更したら、`materials.json`の`assets[].sha256`を更新します。ライセンス認証済みのUnity Projectを使います。

```powershell
& .\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
  -UnityPath <Unity2019.4のUnity.exe> `
  -MaterialRecipePath .\tools\blender-models\CharacterHair\materials.json `
  -GuiProjectPath <licensed-Project> `
  -InstallGuiWorker -StartBackgroundEditor
```

`validate_material.py`で、生成reportと実bundleのshader、property、passを元のbundle・入力recipeと照合します。

```powershell
& <UnityPy入りPython> .\tools\blender-models\CharacterHair\validate_material.py `
  --report <Output\unity-report.json> --baseline <元の採用bundle> `
  --recipe .\tools\blender-models\CharacterHair\materials.json --output <候補receipt.json>
```

## 表示と採用

描画passはForwardBase、ForwardAdd、ShadowCasterです。実ゲームで髪とヒゲの見た目、金具、色選択、表示負荷を比較します。

schema 3の資源では、`CharacterFaces/build_models.py`の`refine-groom-frames --shader-only`で、共有shaderだけを更新します。`--source`に採用元の資源ディレクトリ、`--output`に比較用の出力先、`--shader-report`と`--shader-validation`に生成・検査結果を指定します。CAB名とobject IDを保ち、既存の髪型、材質、画像の参照を引き継ぎます。

同条件の旧版と画像・性能を比較してから採用版へ反映します。旧形式の材質profileには`adopt_material.py`を使います。

## geometry生成の原本コード

`GenerateFlowCoverageSweptProfile.py`がnative source表面から髪・ヒゲのgeometry、全physical frames、skinと明示的なsource triangle/barycentric bindingを生成します。`PrepareGroup.py`が既存のprefix/winding検証と共通`mesh_recipe_codec.py`を使ってUnity入力へ渡します。

共通の毛束画像は、同じgeneratorへ`--workspace <作業先> --output <新しい出力先> --strand-atlas`を渡して作成します。独立した毛の透過・法線・明暗を16タイルへ収め、生成した`MeshRecipe.json`を共通のUnity builderへ渡します。画像は無彩色に保ち、髪色はゲームの色選択で決まります。
`refine-groom-frames`へ`--texture-report <生成report>`を渡すと、資源の参照とsamplerを保って共通画像を差し替えます。

入力にはnative packet・材質・profileを持つ`Input`/`CompletedGroup`を指定し、新しい作業先へ生成します。

`CharacterFaces/build_models.py`の`prepare-groom-inputs --source <毛流れの制作元> --census <原本Census.json> --domain-contract <材質の範囲の照合結果> --output <新しい入力先>`は、`groom-designs.json`の原本を抽出し、骨、全LOD、元のUVと毛流れを照合します。原本MeshはCharacterFacesの`extract --native-index`で取得します。

原本ごとの毛束の幅・浮き上がり・長さは`groom-designs.json`で管理します。生成時は`--asset <原本名> --designs <groom-designs.json> --soft-coverage-pilot --native-hair-domain --domain-contract <照合結果>`を加えます。制作済みの資源へ収める際は、元の三角形との対応を使って既存の頭皮へのフィットを引き継ぎ、全物理フレームの面の向きを確認します。

`prepare-groom-designs --source <制作済み資源> --generations <Generation.json ...> --output <新しいレシピ先>`で取込用Meshを作ります。共通のUnity制作入口へ出力の`recipes`を渡し、`bind-groom-designs --source <同じ制作元> --plan <Plan.json> --report <unity-report.json> --output <毛束の制作先>`で全LODとclothの対応を収めます。

毛束の頂点フレームを仕上げた後、`prepare-groom-composition --source <元の制作済み資源> --fibers <仕上げた毛束の資源> --plan <毛束のPlan.json> --output <組合せ用レシピ先>`で、既存の曲面・装飾・全物理フレームへ新しい毛束を組み合わせます。このレシピもUnity制作入口と`bind-groom-designs`へ渡します。肌の調整済み資源を元にする場合も同じ手順です。

材質の既定値も更新する場合は、`refine-groom-frames`へ`--before-preset <変更前の材質bundle> --after-preset <変更後の材質bundle>`を組で渡します。変更前の既定値と一致する値を更新し、各原本の個別設定を保持します。

```powershell
& <NumPyとPillowのあるPython> .\tools\blender-models\CharacterHair\GenerateFlowCoverageSweptProfile.py `
  --workspace <作業先> --input <検証済みInput.json> --output <新しい数値出力> <元profileと同じ引数>
& <NumPyのあるPython> .\tools\blender-models\CharacterHair\PrepareGroup.py `
  --workspace <rollout作業先> <rollout作業先>\Postprocess\<group>
```

`--workspace`は`tools/asset-authoring/workspace/`または`tools/game-test/artifacts/`の下に指定します。sourceコードと材質recipeをGitで管理し、生成データはこの作業先へ保存します。Unityへの保存・採用は[CharacterFacesの手順](../CharacterFaces/README.md)へ進みます。

追加頂点の元三角形と補間座標を保存し、UVの継ぎ目を跨ぐ曲面にはUBSFB004の共通駆動面を使います。binding形式の詳細は[共通レシピ仕様](../../asset-authoring/README.md#骨格付きfbxのunity資産への変換)を参照してください。

曲面の土台へ割り当てる頂点数は、細い毛のシートを単位に制限します。細い毛の予約量を超える場合は土台の細分化を減らし、元の頂点を共有します。

毛束の幅方向は各physical frameの中心線から求めます。UVや元の表面が不連続になる箇所では毛束を分け、それぞれの先端を細くします。全frameで三角形の向きと面積を検証し、元三角形・補間座標・skinの対応を保存します。
