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

細い毛の描画幅は`_FineAlphaCutoff`で調整し、その幅に断面の法線を合わせます。毛流れ、毛の本数、揺れの頂点は保持します。元の土台と金具の透過には、それぞれの材質の設定を使います。

schema 3の資源では、`CharacterFaces/build_models.py`の`refine-groom-frames --shader-only`で、共有shaderだけを更新します。`--source`に採用元の資源ディレクトリ、`--output`に比較用の出力先、`--shader-report`と`--shader-validation`に生成・検査結果を指定します。CAB名とobject IDを保ち、既存の髪型、材質、画像の参照を引き継ぎます。

geometryと法線だけを仕上げる場合は、shaderの引数を省略します。採用元のshader bundleのSHA256と、対象rendererの材質slotにあるCAB・object IDを照合し、既存のshaderを引き継ぎます。

同条件の旧版と画像・性能を比較してから採用版へ反映します。旧形式の材質profileには`adopt_material.py`を使います。

## geometry生成の原本コード

採用済みカタログの髪型と髭を作り直すときは、`groom-designs.json` の `silhouettes` を編集し、資源と実機で採取した髪・髭と頭部の情報を渡します。刈り上げなど頭皮を見せる髪型は `anchorScalp` で毛束の付け根を頭部に固定します。

```powershell
& <UnityPy入りPython> .\tools\blender-models\CharacterHair\RestyleGrooms.py `
  --source <採用元ディレクトリ> --groom-census <髪・髭のCensus.json> `
  --head-census <頭部のCensus.json> --output <作業ディレクトリ>
```

`--assets` で試作する資源を絞れます。全体を生成するときに `--reuse <試作ディレクトリ>` を渡すと、入力と出力が一致する資源を再利用します。

`GenerateFlowCoverageSweptProfile.py`がnative source表面から髪・ヒゲのgeometry、全physical frames、skinと明示的なsource triangle/barycentric bindingを生成します。`PrepareGroup.py`が既存のprefix/winding検証と共通`mesh_recipe_codec.py`を使ってUnity入力へ渡します。

細毛用の毛束画像は、同じgeneratorへ`--workspace <作業先> --output <新しい出力先> --strand-atlas --atlas-profile <groom-designs.json>`を渡して作成します。主束用は`--workspace <作業先> --output <別の新しい出力先> --strand-atlas --atlas-purpose primary`で生成します。1枚のカードが担う連続した束の透過・法線・明暗を16タイルへ収め、それぞれの`MeshRecipe.json`を[共通のUnity制作入口](../../asset-authoring/README.md#骨格付きfbxのunity資産への変換)の`-MeshRecipePath`へ渡します。画像は無彩色に保ち、髪色はゲームの色選択で決まります。
`refine-groom-frames`へ`--texture-report <細毛用の生成report>`を渡すと、元のsamplerと照合した毛束画像を制作入力へ収めます。元の共有画像は保持します。

入力にはnative packet・材質・profileを持つ`Input`/`CompletedGroup`を指定し、新しい作業先へ生成します。

`groom-designs.json`の断面と毛束寸法は元のrendererの区分から選びます。複数のモデルを生成する場合は同じ入力へ`--workers 4`を渡します。`--asset`で対象を指定でき、完了した形状の`Generation.json`の場所は`Batch.json`に記録されます。レシピの準備と組み合わせでも`--workers`で同時処理数を指定できます。

`CharacterFaces/build_models.py`の`prepare-groom-inputs --source <毛流れの制作元> --census <髪・ヒゲの原本Census.json> --native-index <原本索引JSON> --contact-census <頭部の原本Census.json> --domain-contract <材質の範囲の照合結果> --output <新しい入力先>`は、catalogのGroom参照と実rendererの材質slotから原本を抽出し、骨、全LOD、元のUVと毛流れを照合します。原本索引から材質のsamplerとcloth固定点を収め、頭部のCensusで根元と肌の接触を検証します。一部だけ制作する場合は`--assets <原本名 ...>`を指定します。原本MeshはCharacterFacesの`extract --native-index`で取得します。

原本ごとの毛束の幅・浮き上がり・長さは`groom-designs.json`で管理します。生成時は`--asset <原本名> --designs <groom-designs.json> --soft-coverage-pilot --native-hair-domain --domain-contract <照合結果>`を加えます。制作済みの資源へ収める際は、元の三角形との対応を使って既存の頭皮へのフィットを引き継ぎ、全物理フレームの面の向きを確認します。

`layeredAtlasCards.lockSection`は中間の束の実断面と、元表面の横断幅に対する上限を指定します。UVの継ぎ目を跨ぐ表面の幅を測り、細い元カールにはその幅で収まる束を生成します。外縁の細毛には`fringeWidthMetres`を使います。追加断面のskinは中心の元三角形・補間座標から求め、同じ係数をnative clothのVirtual childへ収めます。

`prepare-groom-designs --source <制作済み資源> --generations <Generation.json ...> --output <新しいレシピ先>`で取込用Meshを作ります。元のUVと物理駆動面から実際の土台の子三角形へ対応させ、連続する毛束のフレームを全保存済みフィットへ反映します。共通のUnity制作入口へ出力の`recipes`を渡し、`bind-groom-designs --source <同じ制作元> --plan <Plan.json> --report <unity-report.json> --output <毛束の制作先>`で全LODとclothの対応を収めます。

`carrierSurface`のprofileでは、数値生成の後に同じgeneratorへ`--curved-core --input <Generation.json> --output <新しい数値出力>`を渡します。根元・装飾・物理駆動点を保持し、所有を照合した土台の面を連続する毛流れの断面へ置き換えます。UVの継ぎ目で毛束を切らず、実際の自由端で先細りにします。元資源の頂点・index・全物理フレームの容量を割り当て上限に使います。

透明な隙間を持つ主束は、元の描画面を残して重ねます。`sourceFacesBySlot`には形状と変形の参照面、`renderReplacementFacesBySlot`には描画を置き換える面を記録し、保存後も元の被覆と容量を照合します。

毛束の頂点フレームを仕上げた後、`prepare-groom-composition --source <元の制作済み資源> --fibers <仕上げた毛束の資源> --plan <毛束のPlan.json> --output <組合せ用レシピ先>`で、曲面・装飾・全物理フレームを組み合わせます。主束を組み合わせる場合は、生成した`Atlas.json`を`--primary-atlas`、そのUnity生成reportを`--primary-atlas-report`へ指定します。根元・装飾・残す細束は原本の材質と画像を保持し、置き換えた主束と新しい細毛をそれぞれ専有の材質slotへ収めます。同じshaderとゲームの色選択を使い、再制作時は用途ごとの専有slotを再利用します。このレシピもUnity制作入口と`bind-groom-designs`へ渡します。肌の調整済み資源を元にする場合も同じ手順です。

元の細毛を活かして曲面を作り直す場合は、`prepare-groom-composition`へ`--retain-fitted-fibers <原本名 ...>`を渡します。使える頂点数と容量の範囲で、輪郭の細毛を優先して毛束単位で保持します。

材質の既定値も更新する場合は、`refine-groom-frames`へ`--before-preset <変更前の材質bundle> --after-preset <変更後の材質bundle>`を渡します。過去の既定値が混在している場合は`--before-preset`を繰り返し指定できます。対象の既定値と一致する値を更新し、各原本の個別設定を保持します。

```powershell
& <NumPyとPillowのあるPython> .\tools\blender-models\CharacterHair\GenerateFlowCoverageSweptProfile.py `
  --workspace <作業先> --input <検証済みInput.json> --output <新しい数値出力> <元profileと同じ引数>
& <NumPyのあるPython> .\tools\blender-models\CharacterHair\PrepareGroup.py `
  --workspace <rollout作業先> <rollout作業先>\Postprocess\<group>
```

`--workspace`は`tools/asset-authoring/workspace/`または`tools/game-test/artifacts/`の下に指定します。sourceコードと材質recipeをGitで管理し、生成データはこの作業先へ保存します。Unityへの保存・採用は[CharacterFacesの手順](../CharacterFaces/README.md)へ進みます。

追加頂点の元三角形と補間座標を保存し、UVの継ぎ目を跨ぐ曲面にはUBSFB004の共通駆動面を使います。binding形式の詳細は[共通レシピ仕様](../../asset-authoring/README.md#骨格付きfbxのunity資産への変換)を参照してください。

曲面の土台へは細い毛のシート単位で頂点を再配分します。宣言した幅で元の容量に収まることを確認し、残す面と置き換える面の境界を原本の三角形・補間座標で検証します。

元画像のUVと毛束の連続座標を別に保持し、UVの継ぎ目には共通の物理駆動面を使います。連続する毛束の先細りは束全体へ適用します。全physical frameで三角形の向きと面積を検証し、元三角形・補間座標・skinの対応を保存します。
