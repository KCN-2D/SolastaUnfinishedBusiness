# 髪・ヒゲの制作

髪とヒゲには共通のshaderを使い、ゲームの色選択、透過、dissolve、金具のmaskを引き継ぎます。`materials.json`にshaderとincludeを登録し、`SourceGroomProgram.cginc`で表面の描画を処理します。
制作と作業データの管理は[asset-authoring](../../asset-authoring/README.md)を参照してください。

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

`adopt_material.py --catalog-directory <比較catalog> --apply`で、比較用catalogの対象bundleと参照profileを更新します。元のbundleとmanifestは保存されます。
同条件の旧版と画像・性能を比較してから採用版へ反映します。bundleの差し替えには`os.replace`を使います。

## geometry生成の原本コード

`GenerateFlowCoverageSweptProfile.py`がnative source表面から髪・ヒゲのgeometry、全physical frames、skinと明示的なsource triangle/barycentric bindingを生成します。`PrepareGroup.py`が既存のprefix/winding検証と共通`mesh_recipe_codec.py`を使ってUnity入力へ渡します。

入力にはnative packet・材質・profileを持つ`Input`/`CompletedGroup`を指定し、新しい作業先へ生成します。

```powershell
& <NumPyとPillowのあるPython> .\tools\blender-models\CharacterHair\GenerateFlowCoverageSweptProfile.py `
  --workspace <作業先> --input <検証済みInput.json> --output <新しい数値出力> <元profileと同じ引数>
& <NumPyのあるPython> .\tools\blender-models\CharacterHair\PrepareGroup.py `
  --workspace <rollout作業先> <rollout作業先>\Postprocess\<group>
```

`--workspace`は`tools/asset-authoring/workspace/`または`tools/game-test/artifacts/`の下に指定します。sourceコードと材質recipeをGitで管理し、生成データはこの作業先へ保存します。Unityへの保存・採用は[CharacterFacesの手順](../CharacterFaces/README.md)へ進みます。

追加頂点の元三角形と補間座標を保存し、UVの継ぎ目を跨ぐ曲面にはUBSFB004の共通駆動面を使います。binding形式の詳細は[共通レシピ仕様](../../asset-authoring/README.md#骨格付きfbxのunity資産への変換)を参照してください。
