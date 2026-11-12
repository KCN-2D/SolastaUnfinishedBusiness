# キャラクターモデルの制作

`production.py`が顔の変形、法線・接線、全表情フレーム、描画を担当し、`build_models.py`が現行bundleの抽出、共有部品の生成条件、Unity入力を管理します。共通の実行・検証方法は[資産制作](../../asset-authoring/README.md)を参照してください。生成ソースとこのREADMEはGitで保持し、元bundle・native packet・入力と生成結果は追跡しない作業先へ分けます。

## 顔の生成

対象の原本、描画部品、骨格の範囲を制作データで指定します。輪郭の調整量と肌の細部は原本ごとに持ち、NPCの年齢や各形態の特徴に合わせます。

最初に頭蓋・頬・顎・首の基礎形状を整え、眼球とまぶた、唇と口内の接続を確認します。正面・斜め・側面を同じカメラ条件で比較してから、表情、LOD、材質へ進みます。造形には各キャラクターのゲーム内の中立姿勢を使います。`captured_neutral_pose`で頭部の参照先、全描画部品の頂点数と骨の順序、変形ウェイトを照合し、`reference_pose_surface_to_rest`で頂点ごとの骨ウェイトを使って保存時の座標へ戻します。造形ガイドは元メッシュの頂点・骨・座標系と対応させ、`production.py`が接触面、共有部品、表情フレームへの適用を管理します。年齢、肌色、化粧、傷などの選択値はゲームが管理します。

造形ガイドには、まぶたの接触面、眼球、まつ毛と首の接続部を保護範囲として宣言します。低LODと表情フレームにも同じガイドを適用し、付属パーツを動かす場合は元の三角形との対応を指定します。変形との境界は周囲の頂点へ滑らかにつなぎます。

`face-designs.json`には、種族・性別・フェイスタイプごとの輪郭、頬、鼻、口、顎の調整量をミリメートルで保存します。`compile-face-designs --census <原本Mesh>/Census.json --reference-index <中立姿勢JSON> --fixtures <対象一覧JSON> --output <新しい出力先>`で、各原本と中立姿勢に対応した`Profiles.json`を作ります。上下の唇が接する範囲は一体として移動し、眼球とまぶたの接触面を保ちます。

造形用の原本は、元bundleからMeshの数値データを直接取り出します。`extract --native-index <元資源の対応表JSON> --census <元モデル一覧JSON> --output <原本Mesh>`で取得し、以降の`--census`には`<原本Mesh>/Census.json`を指定します。描画部品と骨の対応、元bundleのSHA256とMeshの物理IDを記録します。

独立したキャラクター資源へ顔を組み込む場合は、次の引数を使います。元資源の材質、髪、装備と首の接続を引き継ぎ、造形ガイドを適用した候補を作ります。

```text
--workspace <作業先> refine-native-faces --source <元の組み立て済み資源> --native-index <元資源の対応表JSON> --census <原本Mesh>/Census.json --profiles <造形ガイドJSON> --output <候補資源>
```

採用済みの造形を改めるときは、`--previous-profiles <採用元のProfiles.json>`を加えます。原本に対する新旧ガイドの差分を適用し、制作済みの首の接続、低LOD、付属パーツ、表情の補正を引き継ぎます。

肌の細部は`face-designs.json`の`surfaces`で指定します。各原本の`sourceContentKey`、renderer、材質slot、shaderのチャンネル形式と骨格の範囲を照合し、色素と法線の残す強さ、処理する細部の物理寸法を宣言します。共有テクスチャには同じチャンネルの指定を揃えます。

```text
--workspace <作業先> refine-skin-surfaces --source <制作済み資源> --census <原本Mesh>/Census.json --plans <face-designs.json> --output <新しい候補>
```

制作時に元の画像形式と全mipを扱い、範囲外のブロック、材質アルファ、法線の保持チャンネル、samplerを照合します。`bind-skin-surfaces --source <同じ制作元> --fields <作成した資源> --output <最終候補>`で、画像を対象のrendererが持つ材質へ収めます。共有元の画像は保持します。

作業先には`tools/asset-authoring/workspace/`または`tools/game-test/artifacts/`内の実行フォルダーを指定します。以下の`<作業先>`はすべて同じ場所です。最初に`capture --destination <原本フォルダー>`で元のcatalogを保持し、共通引数`--source-directory <原本フォルダー>`でその原本を指定します。

| 工程 | コマンドの引数 |
| --- | --- |
| 対象の抽出 | `--workspace <作業先> scope --scope editable --output <作業先>\Selection.json` |
| 現行Meshの読み取り | `--workspace <作業先> --source-directory <原本フォルダー> extract --selection <作業先>\Selection.json --output <作業先>\CurrentHumanoids` |
| 顔・目・まつ毛・口内の生成 | `--workspace <作業先> generate --profiles <造形ガイドJSON>` |
| Unity入力の作成 | `--workspace <作業先> prepare --generation <作業先>\ModernHumanoids\Index.json --destination <作業先>\UnityInput` |

各行は`build_models.py`に渡す引数です。`extract`と`prepare`には既存のUnityPy環境、`generate`にはBlender同梱Pythonを使います。

```powershell
& .\tools\asset-authoring\workspace\Python\Scripts\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py <引数>

& .\tools\asset-authoring\workspace\Tools\blender-4.5.9-windows-x64\4.5\python\bin\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py <引数>
```

全身メッシュのNPCは`scope --scope npc`で選び、抽出先を`CurrentNpcHumanoids`にして`generate-npc --profiles <造形ガイドJSON>`を実行します。出力は`ModernNpcHumanoids/Index.json`です。どちらの生成も、同じ造形ガイドと部品の適用処理を使います。

`extract`は指定したcatalogのMeshを全チャンネルとフレームから再構成し、canonical fingerprintを照合します。`prepare`はその原本と生成Meshから、参照されているTexture・未変更MeshだけをUnityレシピへ収めます。Unity側では作成、保存、bundle再読み込みの数値と元資産のデータを照合します。

Unityの共通入口`Invoke-UnityAssetBuild.ps1 -MeshRecipePath`には、`Plan.json`の`jobs[].recipe.path`を配列で渡せます。8件程度ずつ実行すると、個別bundleと個別の照合結果を維持したまま`BuildPipeline`の呼び出しをまとめられます。完了したbundle、SHA256、子reportのパスを記録して中断箇所から再開します。

`Invoke-ModelBuild.ps1 -PlanPath <Plan.jsonの配列> -OutputDirectory <完了結果の保存先> -GuiProjectPath <Unity project> -UnityPath <Unity.exe>`は、共通入口を8件ずつ呼び出して結果を`Actual.json`へ記録します。入力のSHA256を確認し、同じ引数で再実行すると完了分を再利用します。保存先に`STOP`ファイルを置くと現在の組の完了後にEditorを通常終了します。再開前にそのファイルを除去します。

`finalize --plan <Plan.json> --results <完了結果.json> --destination <候補catalog>`で、生成したbundleのMesh fingerprintと参照先を照合し、新しいcatalogへまとめます。複数のPlanは`--plan`を繰り返して指定します。

## 体格補正

体格スライダーの補正基準は`SolastaUnfinishedBusiness/BlenderModels/BodyShapes/`で管理します。各骨の座標と変位、ウェイト、補間範囲を保存し、MOD起動時に読み込みます。基準を更新するときは、元資源から抽出した全サンプルと補間結果を照合します。

頭部と胴体の境界は、共通の骨・bind poseと元の接合点から対応を決めます。LODごとに頂点数が異なる境界は、`generate-skin-boundaries --rebuild-collars --inputs <入力JSON> --destination <新しい出力先>`で接合部を再構成します。胴体側の頂点、骨のウェイトと変形フレームを引き継ぎ、頭部のUVに合わせた法線テクスチャを用意します。入力には全LODの原本、骨の順序と実際のサンプラー設定を指定します。

## 手足の生成

`generate-extremities`は、元の頂点・UV・骨のウェイト・変形フレームを保ち、手足の曲面へ頂点を追加します。入力JSONの`rows`には、部品名`id`、`part`（`Hands` / `Feet`）、`lod`、`source={path,sha256}`、`sourceFingerprint`、`rendererToPrefab`を指定します。

```powershell
& <NumPyのあるPython> .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace <作業先> generate-extremities --inputs <入力JSON> --output <新しい出力先>
```

出力は部品名の`.json.gz`、追加頂点の対応表、`Generation.json`です。Unityへの入力には共通のMeshレシピを使います。

## Main テクスチャの生成

`material_fields.py`がUV境界の接続、Mainの色調とmipを計算し、`build_models.py`が入力照合とTaskの保存を担当します。入力JSONのfile参照は、そのJSONからの相対パスまたは絶対パスとSHA256で指定します。

元BC3 cacheを作る場合は、UnityPyとtexture2ddecoderを使う既存環境で次を実行します。入力にはsource bundleのSHA、CAB/PPtr、元encoded SHA、元texture metadataを指定します。

```powershell
& <UnityPyのあるPython> .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace tools/game-test/artifacts/face-main-inputs extract `
  --material-inputs <native入力JSON> --output tools/game-test/artifacts/face-main-inputs
```

生成した`NativeBc3.json`を`nativeBc3Authority`へ指定し、NumPyを使う既存環境で実行します。

```powershell
& <NumPyのあるPython> .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace tools/game-test/artifacts/face-main-source generate-materials `
  --inputs <入力JSON> --maximum-sources 1 `
  --stop-file tools/game-test/artifacts/face-main-source/HoldRequested.flag
```

`BodyMain`はBody原本のpacket、bone順序、Forearms・Hands・Feetの全LOD保護範囲を照合し、`IndependentBodyMain.json`へ結果を記録します。`HeadMain`はHead/Body fingerprint、LOD、sampler source input key、Head Main encoded SHAでIndexの1行を選び、完成したBody Mainを入力にします。

HeadはLOD0を先に生成し、その`Coefficient.json`のpath/SHAをLOD1/2の入力へ指定します。`--maximum-sources`は新しく作るsource数です。入力と出力が一致する完了sourceを再利用します。`--stop-file`へ指定したファイルを作ると、現在のsourceの完了後に停止します。

`HeadMain`の`tonePolicy: regional-head-bone-v1`は、元のHead骨と子骨の影響率で顔と首を滑らかに分け、それぞれの正面表面を同じBody色へ校正します。領域の係数とUV上の影響率を記録し、LOD1/2もLOD0の係数を使います。省略時の`uniform-v3`は従来入力の再生成用です。どちらも同じ色変換式とC1接続を使います。

`MaterialPreparation.json`のTaskをUnity制作入口へ渡し、検証結果とともにcatalogへ組みます。肌色・年齢・傷・フェイスペイントはゲーム側の選択値を使います。

## キャラクター資源の組み立て

`assemble-resources`は、制作済みの骨格・外観部品・顔のアニメーション制御・装備材質を、依存する資源とともに候補catalogへまとめます。参照先を更新し、Textureの圧縮データと各オブジェクトの値を保存後に照合します。

```powershell
& .\tools\asset-authoring\workspace\Python\Scripts\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace <作業先> --source-directory <Meshと材質のcatalog> assemble-resources `
  --prepared-parts <部品の制作結果JSON> --native-index <元資源の対応表JSON> `
  --native-catalog <ゲームのStreamingAssets\aa\catalog.json> `
  --material-definitions <装備材質の定義JSON> --output <候補catalog>
```

部品の制作結果には、各bundleのパスとSHA256、元bundleとの対応、rendererごとの材質パラメーター、参照するMesh・材質catalogのbundleを記録します。元資源の対応表は、bundleのCAB・ファイル名・prefabのアドレスを持ちます。装備材質の定義JSONには`material`を持つレコードの配列を指定します。

組み立て結果は作業先の`ResourceAssembly.json`、候補catalogの宣言は出力先の`CharacterModels.manifest.json`へ保存します。

`repair-resource-bone-orders --source <組み立て結果の作業先> --native-index <元資源の対応表JSON>`は、全prefabの骨対応を元メッシュの骨名ハッシュと照合します。並びが異なる描画部品を修正し、頂点、ウェイト、結合行列、材質、追加した骨スロットを保存後に照合します。`--audit-only`は照合結果だけを出力します。

顔と肌の仕上げは、造形ガイド、全prefabの対応表、元の表情チャンネルを使って実行します。制作した顔と付属部品を組み立て済みの首へつなぎ、接続頂点とフレームを保ちます。顔の色・濃淡・滑らかさは元のテクスチャを使い、Headと首の骨ウェイトに沿って首・胸の校正結果へ補間します。校正済みのBC7ブロックを保持し、顔のRGBと材質アルファを独立したパレットでも符号化して、保存後の復号値を原本と照合します。

```powershell
& .\tools\asset-authoring\workspace\Python\Scripts\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace <候補作業先> prepare-appearance-resources `
  --source <組み立て済み資源の作業先> --native-index <元資源の対応表JSON> `
  --census <元の顔のCensus.json> --material-contexts <材質の対応表JSON> `
  --profiles <造形ガイドJSON> `
  --tone-inputs <元の色校正Taskの作業先> --authoring-sources <制作原本catalog>
```

`GeometryPreparation.json`に完了した形状と入力を記録します。同じ入力で再実行すると形状を再利用し、色の処理から続けます。人型以外の形態と元のパレットは、対応表で区別して保持します。

## モデル資源の配布

`package-resources`は、manifestに載るbundleを分割ZIPにまとめ、`CharacterModels.packs.json`を作成します。

```powershell
& .\tools\asset-authoring\workspace\Python\Scripts\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace <配布作業先> package-resources `
  --manifest <候補catalog>\CharacterModels.manifest.json `
  --output <パック出力先> --release-version <年.月.日.連番>
```

配布パック一覧をmanifestとともにMODへ同梱し、分割ZIPを一覧のURLへ配置します。モデルbundleは生成物として管理します。ゲーム側は一覧のSHA256を照合して展開し、`Mods/SolastaUnfinishedBusinessContent/CharacterModels/`へ保存します。


## 採用と保持

採用前に同じカメラ・姿勢の比較、共有部品、まばたき、口の表情、髪・ヒゲ・首との接続、外観の選択と復元を実ゲームで確認します。bundleの置換は同じフォルダーに新しいファイルを作り、directory entryを置き換えます。

確認後に`publish --catalog <候補catalog> --expected-current-sha256 <現在のmanifestのSHA256>`で反映します。原本catalogは共通引数`--source-directory`で指定します。採用前の原本と候補を照合し、bundleを置換してからmanifestを更新します。途中で失敗した場合は原本へ戻します。

同じ作業先では、入力・制作コード・保存出力が一致する肌の再計算結果を再利用します。採用済み原本もbundleのSHA256を照合し、変更された資源を検証します。

公開名は原本モデル・部位・各モデルのLOD・用途で決めます（例：`human_male_af-as-ca-ho_hands_lod0-1_pn.bundle`）。原本の対応表を`capture`または`publish`の`--name-source-metadata <原本inventory.json>`で指定すると、既存の共有名を保ち、ハッシュ・連番を付けずに名前とmanifest参照を同時更新します。同名衝突は原本との対応を示して停止します。ハッシュは検証用メタデータへ残します。

制作コード、元bundle、NativeSources、採用catalog、品質の記録、代表プレビューを保持します。抽出Mesh、数値レシピ、Unity取込用コピー、失敗・旧候補は、採用結果と原本への参照を確認した後に[共通の整理手順](../../asset-authoring/README.md#作業データの整理)で整理します。

### 旧catalogの整理

採用候補、原本、継続する制作入力、比較画像を保持対象JSONへ登録します。完了した旧catalogを `plan-resource-retirement --retire-directory <旧候補> --reference <保持対象JSON>` へ渡すと、共通の `--workspace` に `PayloadRetirementPlan.json` を作成します。複数の旧候補は `--retire-directory` を繰り返して指定します。保持対象に含まれるファイル・フォルダーと制作記録は残ります。

作成した一覧を[証跡の整理ツール](../../game-test/README.md#証跡の容量管理)へ渡し、対象と容量を確認してから `-Apply` で削除します。新しい候補を採用した時点で旧候補を整理し、完了した候補の全コピーを保存し続けないようにします。

## Main fieldのBC7保存と再読取

`generate-materials`の完了したRGBA全mipとbinary maskを`prepare-material-codec`へ渡します。
この工程は画像Importerを通さず、既存mipをBC7 Best100で一度encodeします。
`UnityAssetBuild/MaterialFieldBc7.cs.in`がEditor処理を担当し、生成結果を全mipで保存・再読み込みして照合します。

codec入力JSONには`schemaVersion=1`、privateの`name`、`tasks`（1～32件）、
`ownerSource`、`nativeAssetAuthority`、`ageScalars`、`scalarAuthority`、
`scalarAuthorityProof`、`holdFile`を指定します。各Taskは完了`Preparation.json`の
`preparation={path,sha256}`と`id`で選びます。既存native BC7 controlを再利用する
場合だけ、`nativeControl={input:{path,sha256},report:{path,sha256},id}`を加えます。

```powershell
python .\tools\blender-models\CharacterFaces\build_models.py --workspace <作業先> `
  prepare-material-codec --inputs <codec入力.json> --destination <新batch>

powershell -File .\tools\asset-authoring\Invoke-UnityAssetBuild.ps1 `
  -UnityPath <Unity.exe> -GuiProjectPath <既存の制作project> `
  -MaterialFieldInputPath <新batch/Input.json> -StartBackgroundEditor -TimeoutSeconds 600

python .\tools\blender-models\CharacterFaces\build_models.py --workspace <作業先> `
  verify-material-codec --inputs <新batch/Input.json> --report <新batch/Result.json> `
  --output-directory <新batch>
```

準備には既存のNumPy環境、圧縮payloadの独立再読取には既存のUnityPy環境を使います。
入力の検査には`-ValidateOnly`を指定します。`holdFile`でpack間の停止を指定できます。
Editorの起動、排他、通常終了、専有する一時source・assetの清掃は共通入口が管理します。
workerの更新には`-InstallGuiWorker`を付けます。再実行には新しい出力先を指定します。

`verify-material-codec`は実bundleの全圧縮byte範囲と計測済みGPU hashを照合し、
`SavedBundleValidation.json`と`ProductionBundleValidation.json`を保存します。
`strictAcceptance`にはmask内の検査値を記録します。

既存BC7とformat・寸法・全mipの長さが同じMainは、次の共通入口で元bundleのresS範囲を置き換えます。

```powershell
python .\tools\blender-models\CharacterFaces\build_models.py --workspace <制作workspace> `
  prepare-resource-spans --inputs <span入力.json> --destination <新catalog作業先>
```

入力には原本manifestとHead source authorityの`{path,sha256}`、変更する全typed keyの
`targets`（実codecのinput・report・完了記録）、同居する保持対象も含めた`priorGpu`、
`expectedHeadMainFields`、`holdFile`を明示します。変更対象のCAB/PPtr・全圧縮mipと、
それ以外のbyte範囲を原本と照合し、`texturePayloadClosure`へ結果を記録します。

DXT5／CrunchのBody atlasは、mesh batchの`encodedTexturePatch`で保存・再読み込みします。

首と胴体の接続には、素材生成で使う共通の対応頂点と骨格を使います。次の入口で、LOD0の接続位置・ボーン重み・物理フレームをそろえたmeshレシピを生成します。生成済みの法線テクスチャがある部品は、その基準となる法線・接線を保ちながら胴体の変形に追従させます。

```powershell
python .\tools\blender-models\CharacterFaces\build_models.py --workspace <制作workspace> `
  generate-skin-boundaries --inputs <接続契約.json> --destination <新しい出力先> `
  --material-catalog <保持したモデル・素材カタログ.json>
```

出力の`Preparation.json`にレシピと元データの対応を記録します。レシピのビルドには[資産制作のmesh batch](../../asset-authoring/README.md)を使います。

顔と身体の肌色は同じ補正基準で仕上げます。`finish_resource_pigments`は、各LODの原本と現在のUV・ボーンの順序を使い、顔のRGBを首・胴体の色へ合わせます。眉や唇などの濃淡と、現在の素材アルファを保ちます。

肌色の補正後は、`finish-skin-lods`でLOD0の首の肌模様を低LODの実際の表面へ転写します。共通のbind poseで対応する三角形を求め、各LODのUVへ焼き込みます。顔側のRGBを保持し、対象の圧縮ブロックと全mipを保存後に照合します。

```powershell
python .\tools\blender-models\CharacterFaces\build_models.py --workspace <新しい出力先> `
  finish-skin-lods --source <肌色補正済みworkspace> `
  --geometry <PigmentCorrections/SavedLodGeometry.json>
```

髪とひげは、prefabの実際のLODGroupと描画部品から検査します。`complete-groom-frames`は、下位LODに残った無地の素材へ同じグループの仕上げ済み素材を割り当て、元のUVチャートと毛流れから描画用データを生成します。髪色を反映するパラメーターも各LODの描画部品へ設定し、保存後に照合します。

```powershell
python .\tools\blender-models\CharacterFaces\build_models.py --workspace <新しい出力先> `
  complete-groom-frames --source <仕上げ済みworkspace>
```
