# キャラクターモデルの制作

`production.py`が顔の変形、法線・接線、全表情フレーム、描画を担当し、`build_models.py`が現行bundleの抽出、共有部品の生成条件、Unity入力を管理します。共通の実行・検証方法は[資産制作](../../asset-authoring/README.md)を参照してください。生成ソースとこのREADMEはGitで保持し、元bundle・native packet・入力と生成結果は追跡しない作業先へ分けます。

## 顔の生成

人間、エルフ、ドワーフ、ハーフリング、ノーム、ティーフリングの顔と、同じ顔の骨格を持つNPCを対象にします。NPCの全身メッシュでは顔の領域へ適用します。ドラゴンボーンとモンスターは各形態の制作データを使います。

顎・頬・鼻・目元を元モデルの骨格寸法に合わせて変形します。共有する眼球、まつ毛、口内は同じ生成条件で扱い、全LODと全フレームの面の向きを保てる強度を一組の部品全体へ適用します。年齢、肌色、化粧、傷などの選択値はゲームが管理します。

まぶたの骨に結び付く接触面、眼球、まつ毛は元の位置・法線・接線と全フレームを保持します。顔の骨を省いた低LODも、同じsourceのLOD0接触領域を引き継ぎます。変形との境界は周囲の頂点へ滑らかにつなぎます。

作業先には`tools/asset-authoring/workspace/`または`tools/game-test/artifacts/`内の実行フォルダーを指定します。以下の`<作業先>`はすべて同じ場所です。最初に`capture --destination <原本フォルダー>`で元のcatalogを保持し、共通引数`--source-directory <原本フォルダー>`でその原本を指定します。

| 工程 | コマンドの引数 |
| --- | --- |
| 対象の抽出 | `--workspace <作業先> scope --scope editable --output <作業先>\Selection.json` |
| 現行Meshの読み取り | `--workspace <作業先> --source-directory <原本フォルダー> extract --selection <作業先>\Selection.json --output <作業先>\CurrentHumanoids` |
| 顔・目・まつ毛・口内の生成 | `--workspace <作業先> generate` |
| Unity入力の作成 | `--workspace <作業先> prepare --generation <作業先>\ModernHumanoids\Index.json --destination <作業先>\UnityInput` |

各行は`build_models.py`に渡す引数です。`extract`と`prepare`には既存のUnityPy環境、`generate`にはBlender同梱Pythonを使います。

```powershell
& .\tools\asset-authoring\workspace\Python\Scripts\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py <引数>

& .\tools\asset-authoring\workspace\Tools\blender-4.5.9-windows-x64\4.5\python\bin\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py <引数>
```

全身メッシュのNPCは`scope --scope npc`で選び、抽出先を`CurrentNpcHumanoids`にして`generate-npc`を実行します。出力は`ModernNpcHumanoids/Index.json`です。

`extract`は指定したcatalogのMeshを全チャンネルとフレームから再構成し、canonical fingerprintを照合します。`prepare`はその原本と生成Meshから、参照されているTexture・未変更MeshだけをUnityレシピへ収めます。Unity側では作成、保存、bundle再読み込みの数値と元資産のデータを照合します。

Unityの共通入口`Invoke-UnityAssetBuild.ps1 -MeshRecipePath`には、`Plan.json`の`jobs[].recipe.path`を配列で渡せます。8件程度ずつ実行すると、個別bundleと個別の照合結果を維持したまま`BuildPipeline`の呼び出しをまとめられます。完了したbundle、SHA256、子reportのパスを記録して中断箇所から再開します。

`Invoke-ModelBuild.ps1 -PlanPath <Plan.jsonの配列> -OutputDirectory <完了結果の保存先> -GuiProjectPath <Unity project> -UnityPath <Unity.exe>`は、共通入口を8件ずつ呼び出して結果を`Actual.json`へ記録します。入力のSHA256を確認し、同じ引数で再実行すると完了分を再利用します。保存先に`STOP`ファイルを置くと現在の組の完了後にEditorを通常終了します。再開前にそのファイルを除去します。

`finalize --plan <Plan.json> --results <完了結果.json> --destination <候補catalog>`で、生成したbundleのMesh fingerprintと参照先を照合し、新しいcatalogへまとめます。複数のPlanは`--plan`を繰り返して指定します。

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

顔と肌の仕上げは、全prefabの対応表と元の表情チャンネルを使って実行します。肌の校正には`surface-local-v4`を使い、目元・眉・口の濃淡と接続部のデータを保ちます。

```powershell
& .\tools\asset-authoring\workspace\Python\Scripts\python.exe `
  .\tools\blender-models\CharacterFaces\build_models.py `
  --workspace <候補作業先> prepare-appearance-resources `
  --source <組み立て済み資源の作業先> --native-index <元資源の対応表JSON> `
  --census <元の顔のCensus.json> --material-contexts <材質の対応表JSON> `
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

公開名は原本モデル・部位・各モデルのLOD・用途で決めます（例：`human_male_af-as-ca-ho_hands_lod0-1_pn.bundle`）。原本の対応表を`capture`または`publish`の`--name-source-metadata <原本inventory.json>`で指定すると、既存の共有名を保ち、ハッシュ・連番を付けずに名前とmanifest参照を同時更新します。同名衝突は原本との対応を示して停止します。ハッシュは検証用メタデータへ残します。

制作コード、元bundle、NativeSources、採用catalog、品質の記録、代表プレビューを保持します。抽出Mesh、数値レシピ、Unity取込用コピー、失敗・旧候補は、採用結果と原本への参照を確認した後に[共通の整理手順](../../asset-authoring/README.md#作業データの整理)で整理します。

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
