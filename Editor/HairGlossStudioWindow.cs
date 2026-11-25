using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    /// <summary>
    /// Main EditorWindow for Hair Gloss Studio, providing target hair detection,
    /// preset & parameter controls, live previews, and non-destructive lilToon material application.
    /// </summary>
    public class HairGlossStudioWindow : EditorWindow
    {
        private static readonly string[] ResolutionLabels = { "512", "1024", "2048" };
        private static readonly int[] ResolutionValues = { 512, 1024, 2048 };
        private static readonly string[] BlendModeLabels = { "通常 (Normal)", "加算 (Add)", "スクリーン (Screen)", "乗算 (Multiply)" };

        private GameObject targetRoot;
        private bool includeAllRenderers;
        private bool targetsFoldout = true;
        private bool advancedFoldout;
        private bool liveScenePreview;
        private Vector2 scrollPos;

        private HairGlossSettings settings = new HairGlossSettings();
        private readonly List<HairMaterialTarget> targets = new List<HairMaterialTarget>();

        private HairGlossPreviewRenderer previewRenderer;
        private HairGlossBackupStore backupStore;
        private string statusMessage;
        private MessageType statusMessageType = MessageType.Info;
        private static string SettingsKey => $"HairGlossStudio.Settings.{Application.dataPath}";

        [MenuItem("Tools/Hair Gloss Studio (髪のツヤ設定)", false, 100)]
        [MenuItem("Tools/Chill-Bell Style/Hair Gloss Studio (髪のツヤ設定)", false, 100)]
        public static void ShowWindow()
        {
            HairGlossStudioWindow window = GetWindow<HairGlossStudioWindow>("Hair Gloss Studio");
            window.minSize = new Vector2(460f, 640f);
            window.Show();
        }

        private void OnEnable()
        {
            string savedSettings = EditorPrefs.GetString(SettingsKey, string.Empty);
            if (!string.IsNullOrEmpty(savedSettings))
            {
                try { settings = JsonUtility.FromJson<HairGlossSettings>(savedSettings); }
                catch (ArgumentException) { settings = new HairGlossSettings(); }
            }
            if (settings == null)
            {
                settings = new HairGlossSettings();
            }

            if (previewRenderer == null)
            {
                previewRenderer = new HairGlossPreviewRenderer();
            }

            if (targetRoot == null && Selection.activeGameObject != null)
            {
                targetRoot = Selection.activeGameObject;
                LoadTargets();
            }
            else
            {
                RebuildPreviewTextures();
            }
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));
            if (previewRenderer != null)
            {
                previewRenderer.Dispose();
                previewRenderer = null;
            }
            liveScenePreview = false;
        }

        private void OnGUI()
        {
            EnsureInitialized();

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            EditorGUILayout.Space(6);

            DrawHeader();
            EditorGUILayout.Space(6);

            DrawTargetSelectionSection();
            EditorGUILayout.Space(8);

            DrawPreviewSection();
            EditorGUILayout.Space(8);

            EditorGUI.BeginChangeCheck();
            DrawPresetsAndCustomizationSection();
            bool settingsChanged = EditorGUI.EndChangeCheck();

            if (settingsChanged)
            {
                EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));
                RebuildPreviewTextures();
                if (liveScenePreview)
                {
                    previewRenderer.UpdateScenePreview(targets, settings, GetBackupStoreIfExists());
                }
            }

            EditorGUILayout.Space(8);
            DrawActionSection();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(statusMessage, statusMessageType);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.EndScrollView();
        }

        private void EnsureInitialized()
        {
            if (settings == null)
            {
                settings = new HairGlossSettings();
            }

            if (previewRenderer == null)
            {
                previewRenderer = new HairGlossPreviewRenderer();
                RebuildPreviewTextures();
            }
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14
                };
                EditorGUILayout.LabelField("Hair Gloss Studio  -  髪のツヤ自動生成ツール", titleStyle);
                EditorGUILayout.LabelField(
                    "lilToon の髪マテリアルに対し、毛流れ解析・天使の輪 MatCap・異方性反射を非破壊で設定します。",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawTargetSelectionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("1. 対象オブジェクト設定", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GameObject newRoot = (GameObject)EditorGUILayout.ObjectField(
                        "アバター または 髪",
                        targetRoot,
                        typeof(GameObject),
                        true);

                    if (newRoot != targetRoot)
                    {
                        previewRenderer.ClearScenePreview();
                        liveScenePreview = false;
                        targetRoot = newRoot;
                        LoadTargets();
                    }

                    if (GUILayout.Button("読み込み", GUILayout.Width(84)))
                    {
                        previewRenderer.ClearScenePreview();
                        LoadTargets();
                    }
                }

                EditorGUI.BeginChangeCheck();
                includeAllRenderers = EditorGUILayout.ToggleLeft("髪以外のマテリアルも一覧に表示する", includeAllRenderers);
                if (EditorGUI.EndChangeCheck())
                {
                    previewRenderer.ClearScenePreview();
                    LoadTargets();
                }

                if (targetRoot == null)
                {
                    EditorGUILayout.HelpBox("シーン上のアバターまたは髪オブジェクトをドラッグ＆ドロップしてください。", MessageType.Info);
                    return;
                }

                if (targets.Count == 0)
                {
                    EditorGUILayout.HelpBox("対象の Renderer / Material が見つかりませんでした。", MessageType.Warning);
                    return;
                }

                int selectedLilCount = 0;
                bool hasMatCapConflict = false;

                foreach (HairMaterialTarget t in targets)
                {
                    if (t.isSelected && t.isLilToon)
                    {
                        selectedLilCount++;
                        HairGlossBackupStore targetStore = HairGlossBackupStore.FindForTarget(t.renderer, t.submeshIndex);
                        Material baseMat = targetStore != null
                            ? targetStore.GetOriginalMaterial(t.renderer, t.submeshIndex)
                            : t.originalMaterial;
                        if (!LilToonMaterialConfigurator.TryResolveMatCapSlot(baseMat, MatCapSlotMode.Auto, out _))
                            hasMatCapConflict = true;
                    }
                }

                targetsFoldout = EditorGUILayout.Foldout(
                    targetsFoldout,
                    $"検出された髪マテリアル一覧 ({selectedLilCount}/{targets.Count} 選択中)",
                    true);

                if (targetsFoldout)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("lilToon髪をすべて選択", EditorStyles.miniButton))
                        {
                            foreach (HairMaterialTarget t in targets)
                            {
                                t.isSelected = t.isLilToon;
                            }
                            OnTargetSelectionChanged();
                        }
                        if (GUILayout.Button("選択解除", EditorStyles.miniButton))
                        {
                            foreach (HairMaterialTarget t in targets)
                            {
                                t.isSelected = false;
                            }
                            OnTargetSelectionChanged();
                        }
                    }

                    foreach (HairMaterialTarget t in targets)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUI.BeginDisabledGroup(!t.isLilToon);
                            bool nextSelected = EditorGUILayout.Toggle(t.isSelected, GUILayout.Width(18));
                            if (nextSelected != t.isSelected)
                            {
                                t.isSelected = nextSelected;
                                OnTargetSelectionChanged();
                            }
                            EditorGUI.EndDisabledGroup();

                            EditorGUILayout.LabelField(t.DisplayName, GUILayout.MinWidth(140));

                            GUIContent shaderTag = t.isLilToon
                                ? new GUIContent("lilToon")
                                : new GUIContent("非対応");
                            Color prevColor = GUI.color;
                            GUI.color = t.isLilToon ? new Color(0.65f, 1f, 0.72f) : new Color(1f, 0.65f, 0.55f);
                            GUILayout.Label(shaderTag, EditorStyles.miniButton, GUILayout.Width(56));

                            bool hasBackup = HairGlossBackupStore.FindForTarget(t.renderer, t.submeshIndex) != null;
                            GUI.color = hasBackup ? new Color(0.65f, 0.85f, 1f) : new Color(0.85f, 0.85f, 0.85f);
                            GUILayout.Label(hasBackup ? "適用済み" : "オリジナル", EditorStyles.miniButton, GUILayout.Width(64));
                            GUI.color = prevColor;
                        }
                    }
                }

                if (hasMatCapConflict && settings.matCapSlot == MatCapSlotMode.Auto)
                {
                    EditorGUILayout.HelpBox("空きMatCapスロットがない対象は、既存MatCapを保持してMatCap設定のみスキップします。置き換える場合は詳細設定で1st/2ndを明示してください。", MessageType.Warning);
                }
                else if (settings.matCapSlot != MatCapSlotMode.Auto)
                {
                    EditorGUILayout.HelpBox("MatCapスロットを明示指定しています。選択したスロットの既存設定は生成マテリアルで置き換えられます。既存設定を保持する場合はAutoを選択してください。", MessageType.Info);
                }
            }
        }

        private void DrawPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("2. リアルタイムプレビュー", EditorStyles.boldLabel);

                float availableWidth = Mathf.Max(340f, position.width - 48f);
                float tileSize = Mathf.Clamp((availableWidth - 24f) / 4f, 64f, 112f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DrawPreviewTile("仕上がりイメージ", previewRenderer.DomePreview, tileSize);
                    DrawPreviewTile("天使の輪 MatCap", previewRenderer.MatCapPreview, tileSize);
                    DrawPreviewTile("毛流れ法線マップ", previewRenderer.NormalPreview, tileSize);
                    DrawPreviewTile("ツヤマスク", previewRenderer.MaskPreview, tileSize);
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Space(4);
                EditorGUI.BeginDisabledGroup(targetRoot == null || targets.Count == 0);
                EditorGUI.BeginChangeCheck();
                liveScenePreview = EditorGUILayout.ToggleLeft(
                    "Sceneビューにリアルタイムプレビュー反映（一時マテリアルを使用）",
                    liveScenePreview);
                if (EditorGUI.EndChangeCheck())
                {
                    if (liveScenePreview)
                    {
                        previewRenderer.UpdateScenePreview(targets, settings, GetBackupStoreIfExists());
                    }
                    else
                    {
                        previewRenderer.ClearScenePreview();
                    }
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private static void DrawPreviewTile(string label, Texture2D tex, float size)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(size + 6f)))
            {
                Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                if (tex != null)
                {
                    EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.18f, 1f));
                }

                GUIStyle centeredMini = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    normal = { textColor = EditorStyles.label.normal.textColor }
                };
                EditorGUILayout.LabelField(label, centeredMini, GUILayout.Width(size));
            }
        }

        private void DrawPresetsAndCustomizationSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("3. かんたんプリセット＆カスタマイズ", EditorStyles.boldLabel);

                // ツヤの強さ
                DrawSegmentedPresetRow(
                    "ツヤの強さ",
                    new[] { "弱", "中", "強" },
                    settings.intensityPreset == GlossIntensityPreset.Custom ? -1 : (int)settings.intensityPreset,
                    index => settings.ApplyIntensityPreset((GlossIntensityPreset)index));

                float nextIntensity = EditorGUILayout.Slider("ツヤの強度", settings.glossIntensity, 0f, 2f);
                if (!Mathf.Approximately(nextIntensity, settings.glossIntensity))
                {
                    settings.glossIntensity = nextIntensity;
                    settings.intensityPreset = GlossIntensityPreset.Custom;
                }

                EditorGUILayout.Space(4);

                // 質感
                DrawSegmentedPresetRow(
                    "質感",
                    new[] { "グロス", "マット" },
                    settings.finishPreset == GlossFinishPreset.Custom ? -1 : (int)settings.finishPreset,
                    index => settings.ApplyFinishPreset((GlossFinishPreset)index));

                float nextSmooth = EditorGUILayout.Slider("滑らかさ (Smoothness)", settings.smoothness, 0f, 1f);
                if (!Mathf.Approximately(nextSmooth, settings.smoothness))
                {
                    settings.smoothness = nextSmooth;
                    settings.finishPreset = GlossFinishPreset.Custom;
                }

                EditorGUILayout.Space(4);

                // ツヤの幅
                DrawSegmentedPresetRow(
                    "ツヤの幅",
                    new[] { "細い", "普通", "太い" },
                    settings.widthPreset == GlossWidthPreset.Custom ? -1 : (int)settings.widthPreset,
                    index => settings.ApplyWidthPreset((GlossWidthPreset)index));

                float nextWidth = EditorGUILayout.Slider("ハイライト幅", settings.highlightWidth, 0.05f, 1f);
                if (!Mathf.Approximately(nextWidth, settings.highlightWidth))
                {
                    settings.highlightWidth = nextWidth;
                    settings.anisotropyBitangentWidth = Mathf.Lerp(0.05f, 0.5f, nextWidth);
                    settings.widthPreset = GlossWidthPreset.Custom;
                }

                EditorGUILayout.Space(4);

                // 毛筋の凹凸
                DrawSegmentedPresetRow(
                    "毛筋の凹凸",
                    new[] { "なし", "控えめ", "くっきり" },
                    settings.strandPreset == StrandBumpPreset.Custom ? -1 : (int)settings.strandPreset,
                    index => settings.ApplyStrandPreset((StrandBumpPreset)index));

                float nextBump = EditorGUILayout.Slider("毛筋の立体感 (Bump)", settings.strandBumpScale, 0f, 2f);
                if (!Mathf.Approximately(nextBump, settings.strandBumpScale))
                {
                    settings.strandBumpScale = nextBump;
                    settings.strandPreset = StrandBumpPreset.Custom;
                }

                settings.strandFrequency = EditorGUILayout.Slider("毛筋の細かさ (密度)", settings.strandFrequency, 10f, 200f);

                EditorGUILayout.Space(6);

                // 天使の輪
                EditorGUILayout.LabelField("天使の輪の調整", EditorStyles.boldLabel);
                settings.enableAngelRing = EditorGUILayout.Toggle("天使の輪を有効にする", settings.enableAngelRing);
                using (new EditorGUI.DisabledGroupScope(!settings.enableAngelRing))
                {
                    settings.angelRingHeight = EditorGUILayout.Slider("位置の高さ", settings.angelRingHeight, -0.5f, 0.8f);
                    settings.angelRingArch = EditorGUILayout.Slider("アーチのカーブ", settings.angelRingArch, 0f, 1f);
                    settings.angelRingSharpness = EditorGUILayout.Slider("輪郭のくっきり度", settings.angelRingSharpness, 0.1f, 1f);
                }

                Vector4 uv1 = settings.matCapBlendUV1;
                uv1.y = EditorGUILayout.Slider("UV1垂直固定 (_MatCapBlendUV1.y)", uv1.y, 0f, 1f);
                settings.matCapBlendUV1 = uv1;

                EditorGUILayout.Space(6);

                // ツヤの色
                EditorGUILayout.LabelField("ツヤの色・アクセントカラー", EditorStyles.boldLabel);
                settings.glossColor = EditorGUILayout.ColorField("ツヤの色", settings.glossColor);
                settings.mainTexColorBlend = EditorGUILayout.Slider("元の髪色との馴染み度", settings.mainTexColorBlend, 0f, 1f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("純白", EditorStyles.miniButton))
                    {
                        settings.glossColor = Color.white;
                        settings.mainTexColorBlend = 0.05f;
                    }
                    if (GUILayout.Button("髪色に馴染ませる", EditorStyles.miniButton))
                    {
                        settings.glossColor = Color.white;
                        settings.mainTexColorBlend = 0.65f;
                    }
                    if (GUILayout.Button("ウォームゴールド", EditorStyles.miniButton))
                    {
                        settings.glossColor = new Color(1f, 0.90f, 0.68f, 1f);
                        settings.mainTexColorBlend = 0.1f;
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("クールシアン", EditorStyles.miniButton))
                    {
                        settings.glossColor = new Color(0.68f, 0.92f, 1f, 1f);
                        settings.mainTexColorBlend = 0.08f;
                    }
                    if (GUILayout.Button("桜ピンク", EditorStyles.miniButton))
                    {
                        settings.glossColor = new Color(1f, 0.76f, 0.88f, 1f);
                        settings.mainTexColorBlend = 0.08f;
                    }
                    if (GUILayout.Button("ラベンダー", EditorStyles.miniButton))
                    {
                        settings.glossColor = new Color(0.84f, 0.74f, 1f, 1f);
                        settings.mainTexColorBlend = 0.08f;
                    }
                }

                EditorGUILayout.Space(6);

                // 詳細設定
                advancedFoldout = EditorGUILayout.Foldout(advancedFoldout, "詳細設定 (毛流れ解析・異方性・出力設定)", true);
                if (advancedFoldout)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        int resIdx = Array.IndexOf(ResolutionValues, settings.textureResolution);
                        if (resIdx < 0) resIdx = 1;
                        int nextResIdx = EditorGUILayout.Popup("生成テクスチャ解像度", resIdx, ResolutionLabels);
                        settings.textureResolution = ResolutionValues[nextResIdx];

                        settings.matCapSlot = (MatCapSlotMode)EditorGUILayout.EnumPopup("使用 MatCap スロット", settings.matCapSlot);
                        EditorGUILayout.LabelField("Auto: 空き1st → 空き2nd。両方使用中はMatCapのみスキップ。", EditorStyles.wordWrappedMiniLabel);
                        settings.matCapBlendMode = EditorGUILayout.Popup("MatCap 合成モード", settings.matCapBlendMode, BlendModeLabels);
                        settings.matCapNormalStrength = EditorGUILayout.Slider("MatCap ノーマル強度", settings.matCapNormalStrength, 0f, 2f);

                        EditorGUILayout.Space(3);
                        settings.autoAnalyzeFlow = EditorGUILayout.Toggle("毛流れ自動解析 (UV + テクスチャ)", settings.autoAnalyzeFlow);
                        using (new EditorGUI.DisabledGroupScope(!settings.autoAnalyzeFlow))
                        {
                            settings.flowAnalyzeWeight = EditorGUILayout.Slider("テクスチャ勾配解析の重み", settings.flowAnalyzeWeight, 0f, 1f);
                        }

                        EditorGUILayout.Space(3);
                        settings.enableAnisotropy = EditorGUILayout.Toggle("異方性反射 (Anisotropy) を有効化", settings.enableAnisotropy);
                        using (new EditorGUI.DisabledGroupScope(!settings.enableAnisotropy))
                        {
                            settings.anisotropyTangentWidth = EditorGUILayout.Slider("接線方向の幅 (Tangent)", settings.anisotropyTangentWidth, 0.05f, 2f);
                            settings.anisotropyBitangentWidth = EditorGUILayout.Slider("従法線方向の幅 (Bitangent)", settings.anisotropyBitangentWidth, 0.02f, 1f);
                            settings.anisotropyShift = EditorGUILayout.Slider("ハイライト位置オフセット", settings.anisotropyShift, -1f, 1f);
                            settings.anisotropyShiftNoiseScale = EditorGUILayout.Slider("位置ノイズ強度", settings.anisotropyShiftNoiseScale, 0f, 1f);
                            settings.anisotropySpecularStrength = EditorGUILayout.Slider("異方性 光沢強度", settings.anisotropySpecularStrength, 0f, 2f);
                            settings.anisotropyMatCapStrength = EditorGUILayout.Slider("異方性 MatCap連動", settings.anisotropyMatCapStrength, 0f, 2f);
                        }

                        EditorGUILayout.Space(3);
                        settings.rootFalloff = EditorGUILayout.Slider("根元フェードアウト", settings.rootFalloff, 0f, 0.5f);
                        settings.tipFalloff = EditorGUILayout.Slider("毛先フェードアウト", settings.tipFalloff, 0f, 0.5f);
                        settings.shadowMaskStrength = EditorGUILayout.Slider("影部分のツヤ抑制", settings.shadowMaskStrength, 0f, 1f);

                        EditorGUILayout.Space(3);
                        settings.outputFolder = EditorGUILayout.TextField("出力先フォルダ", settings.outputFolder);

                        if (GUILayout.Button("パラメータを初期値にリセット", EditorStyles.miniButton))
                        {
                            settings = new HairGlossSettings();
                        }
                    }
                }
            }
        }

        private static void DrawSegmentedPresetRow(
            string label,
            string[] options,
            int activeIndex,
            Action<int> onSelect)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                for (int i = 0; i < options.Length; i++)
                {
                    GUIStyle style = i == 0
                        ? EditorStyles.miniButtonLeft
                        : (i == options.Length - 1 ? EditorStyles.miniButtonRight : EditorStyles.miniButtonMid);
                    if (options.Length == 1) style = EditorStyles.miniButton;

                    bool isActive = i == activeIndex;
                    Color prevBg = GUI.backgroundColor;
                    if (isActive)
                    {
                        GUI.backgroundColor = new Color(0.45f, 0.78f, 1f, 1f);
                    }

                    if (GUILayout.Button(options[i], style, GUILayout.Height(22)))
                    {
                        onSelect?.Invoke(i);
                    }

                    GUI.backgroundColor = prevBg;
                }
            }
        }

        private void DrawActionSection()
        {
            int selectedCount = 0;
            foreach (HairMaterialTarget t in targets)
            {
                if (t.isSelected && t.isLilToon)
                {
                    selectedCount++;
                }
            }

            HairGlossBackupStore existingStore = GetBackupStoreIfExists();
            bool canRevert = (existingStore != null && existingStore.HasBackupForRoot(targetRoot))
                             || previewRenderer.HasActiveScenePreview;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("4. 適用・復元", EditorStyles.boldLabel);

                EditorGUI.BeginDisabledGroup(targetRoot == null || selectedCount == 0);
                Color prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.42f, 0.85f, 0.62f, 1f);
                if (GUILayout.Button("✨ ツヤを適用する（マテリアル複製・非破壊）", GUILayout.Height(36)))
                {
                    ApplyGlossToSelectedTargets();
                }
                GUI.backgroundColor = prevBg;
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(4);

                EditorGUI.BeginDisabledGroup(!canRevert);
                if (GUILayout.Button("↩ 元のマテリアルに戻す", GUILayout.Height(26)))
                {
                    RevertTargetsToOriginal();
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private void LoadTargets()
        {
            targets.Clear();
            if (targetRoot == null)
            {
                previewRenderer.InvalidateAnalysisCache();
                RebuildPreviewTextures();
                return;
            }

            targets.AddRange(HairTargetDetector.DetectTargets(targetRoot, includeAllRenderers));

            foreach (HairMaterialTarget t in targets)
            {
                HairGlossBackupStore store = HairGlossBackupStore.FindForTarget(t.renderer, t.submeshIndex);
                if (store != null)
                {
                    if (store.TryGetRecord(t.renderer, t.submeshIndex, out RendererMaterialRecord rec) &&
                        rec.originalMaterial != null)
                    {
                        t.originalMaterial = rec.originalMaterial;
                    }
                }
            }

            previewRenderer.InvalidateAnalysisCache();
            RebuildPreviewTextures();
            statusMessage = targets.Count > 0
                ? $"{targets.Count} 件のマテリアルスロットを読み込みました。"
                : "対象のマテリアルが見つかりませんでした。";
            statusMessageType = targets.Count > 0 ? MessageType.Info : MessageType.Warning;
        }

        private void OnTargetSelectionChanged()
        {
            previewRenderer.InvalidateAnalysisCache();
            RebuildPreviewTextures();
            if (liveScenePreview)
            {
                previewRenderer.UpdateScenePreview(targets, settings, GetBackupStoreIfExists());
            }
        }

        private void RebuildPreviewTextures()
        {
            HairMaterialTarget reference = null;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].isSelected && targets[i].isLilToon)
                {
                    reference = targets[i];
                    break;
                }
            }

            if (reference == null && targets.Count > 0)
            {
                reference = targets[0];
            }

            previewRenderer.RebuildPreviews(reference, settings);
        }

        private void ApplyGlossToSelectedTargets(Func<string, float, bool> cancel = null)
        {
            if (targetRoot == null)
            {
                return;
            }

            if (!HairTextureGenerator.TryNormalizeOutputFolder(settings.outputFolder, out string rootFolder, out string folderError))
            {
                statusMessage = folderError;
                statusMessageType = MessageType.Error;
                return;
            }

            // Clear transient preview before touching real materials
            previewRenderer.ClearScenePreview();
            liveScenePreview = false;

            var selected = new List<HairMaterialTarget>();
            foreach (HairMaterialTarget t in targets)
            {
                if (t != null && t.isSelected && t.isLilToon && t.renderer != null)
                {
                    selected.Add(t);
                }
            }

            if (selected.Count == 0)
            {
                statusMessage = "適用対象の lilToon マテリアルが選択されていません。";
                statusMessageType = MessageType.Warning;
                return;
            }

            foreach (HairMaterialTarget target in selected)
            {
                HairGlossBackupStore.FindForTarget(target.renderer, target.submeshIndex, out bool ambiguous);
                if (ambiguous)
                {
                    statusMessage = "複数のバックアップが対象に一致するため、適用を中止しました。バックアップを確認してください。";
                    statusMessageType = MessageType.Error;
                    return;
                }
            }

            string avatarFolderName = HairTextureGenerator.SanitizeFileName(targetRoot.name);
            string targetOutputFolder = $"{rootFolder}/{avatarFolderName}";
            HairGlossBackupStore defaultStore;
            try
            {
                HairTextureGenerator.EnsureFolderExists(targetOutputFolder);
                defaultStore = HairGlossBackupStore.LoadOrCreate();
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
            {
                statusMessage = exception.Message;
                statusMessageType = MessageType.Error;
                return;
            }
            int appliedCount = 0;
            bool cancelled = false;
            var analysisCache = new HairFlowAnalyzer.AnalysisCache();
            float flowWeight = settings.autoAnalyzeFlow ? settings.flowAnalyzeWeight : 0f;
            foreach (HairMaterialTarget target in selected)
            {
                HairGlossBackupStore store = HairGlossBackupStore.FindForTarget(target.renderer, target.submeshIndex) ?? defaultStore;
                Material originalMat = store.GetOriginalMaterial(target.renderer, target.submeshIndex);
                if (originalMat == null) continue;
                Texture2D mainTex = originalMat.HasProperty("_MainTex") ? originalMat.GetTexture("_MainTex") as Texture2D : null;
                analysisCache.Retain(HairFlowAnalyzer.GetAnalysisCacheKey(target.mesh, target.submeshIndex, mainTex, settings.textureResolution, flowWeight));
            }
            if (cancel == null)
                cancel = (message, progress) => EditorUtility.DisplayCancelableProgressBar("Hair Gloss Studio", message, progress);
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Hair Gloss Studio");

            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    HairMaterialTarget target = selected[i];
                    HairGlossBackupStore store = HairGlossBackupStore.FindForTarget(target.renderer, target.submeshIndex) ?? defaultStore;
                    bool ShouldCancel(string stage, float fraction)
                    {
                        cancelled = cancel($"{stage} ({i + 1}/{selected.Count}): {target.DisplayName}", (i + fraction) / selected.Count);
                        return cancelled;
                    }
                    if (ShouldCancel("毛流れ解析", 0f)) break;

                    Material originalMat = store.GetOriginalMaterial(target.renderer, target.submeshIndex);
                    if (originalMat == null)
                    {
                        continue;
                    }

                    Texture2D mainTex = originalMat.HasProperty("_MainTex")
                        ? originalMat.GetTexture("_MainTex") as Texture2D
                        : null;
                    Color baseColor = originalMat.HasProperty("_Color")
                        ? originalMat.GetColor("_Color")
                        : Color.white;

                    string analysisKey = HairFlowAnalyzer.GetAnalysisCacheKey(target.mesh, target.submeshIndex, mainTex, settings.textureResolution, flowWeight);
                    HairFlowAnalyzer.AnalysisResult analysis = analysisCache.GetOrAnalyze(
                        target.mesh,
                        target.submeshIndex,
                        mainTex,
                        settings.textureResolution,
                        flowWeight);

                    Color avgBase = new Color(
                        analysis.averageBaseColor.r * baseColor.r,
                        analysis.averageBaseColor.g * baseColor.g,
                        analysis.averageBaseColor.b * baseColor.b,
                        1f);

                    string baseAssetName = $"{originalMat.name}_{target.renderer.name}_S{target.submeshIndex}";
                    bool ownsAssets = store.OwnsGeneratedAssets(target.renderer, target.submeshIndex);
                    store.TryGetRecord(target.renderer, target.submeshIndex, out RendererMaterialRecord existingRecord);
                    Texture2D rawNormal = null;
                    Texture2D rawMatCap = null;
                    Texture2D rawMask = null;
                    Texture2D rawTangent = null;
                    HairTextureGenerator.SavedTextureSet savedTextures;
                    try
                    {
                        if (ShouldCancel("ノーマル生成", 0.2f)) break;
                        rawNormal = HairTextureGenerator.GenerateFlowNormalMap(analysis, settings);
                        if (ShouldCancel("MatCap生成", 0.35f)) break;
                        rawMatCap = HairTextureGenerator.GenerateGlossMatCap(settings, avgBase);
                        if (ShouldCancel("マスク生成", 0.5f)) break;
                        rawMask = HairTextureGenerator.GenerateGlossMask(analysis, settings);
                        if (ShouldCancel("接線マップ生成", 0.65f)) break;
                        rawTangent = HairTextureGenerator.GenerateAnisotropyTangentMap(analysis);
                        if (ShouldCancel("テクスチャ保存・マテリアル適用", 0.8f)) break;
                        savedTextures = HairTextureGenerator.SaveTexturesAsAssets(
                            targetOutputFolder, baseAssetName, rawNormal, rawMatCap, rawMask, rawTangent,
                            settings, ownsAssets ? existingRecord.generatedTexturePaths : null);
                    }
                    finally
                    {
                        if (rawNormal != null) DestroyImmediate(rawNormal);
                        if (rawMatCap != null) DestroyImmediate(rawMatCap);
                        if (rawMask != null) DestroyImmediate(rawMask);
                        if (rawTangent != null) DestroyImmediate(rawTangent);
                        analysisCache.Release(analysisKey);
                        analysis = null;
                    }

                    string matAssetPath = ownsAssets ? AssetDatabase.GetAssetPath(existingRecord.generatedMaterial) : AssetDatabase.GenerateUniqueAssetPath(Path.Combine(
                        targetOutputFolder,
                        $"{HairTextureGenerator.SanitizeFileName(baseAssetName)}_HairGloss.mat").Replace('\\', '/'));

                    Material clonedMat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
                    if (clonedMat == null)
                    {
                        clonedMat = new Material(originalMat);
                        AssetDatabase.CreateAsset(clonedMat, matAssetPath);
                    }
                    else
                    {
                        clonedMat.shader = originalMat.shader;
                        clonedMat.CopyPropertiesFromMaterial(originalMat);
                    }

                    LilToonMaterialConfigurator.ConfigureMaterial(
                        clonedMat,
                        settings,
                        savedTextures.normalMap,
                        savedTextures.matCap,
                        savedTextures.mask,
                        savedTextures.tangentMap);

                    Material[] sharedMats = target.renderer.sharedMaterials;
                    if (sharedMats != null && target.submeshIndex >= 0 && target.submeshIndex < sharedMats.Length)
                    {
                        Undo.RecordObject(target.renderer, "Apply Hair Gloss Studio");
                        sharedMats[target.submeshIndex] = clonedMat;
                        target.renderer.sharedMaterials = sharedMats;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(target.renderer);
                        EditorUtility.SetDirty(target.renderer);

                        store.RegisterBackup(
                            target.renderer,
                            target.submeshIndex,
                            originalMat,
                            clonedMat,
                            new[]
                            {
                                savedTextures.normalMapPath,
                                savedTextures.matCapPath,
                                savedTextures.maskPath,
                                savedTextures.tangentMapPath
                            });

                        target.originalMaterial = originalMat;
                        target.currentMaterial = clonedMat;
                        appliedCount++;
                    }
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                analysisCache.Clear();
                Undo.CollapseUndoOperations(undoGroup);
                EditorUtility.ClearProgressBar();
            }

            statusMessage = cancelled
                ? $"キャンセルしました。適用済みの {appliedCount} 件はバックアップとともに保持されます。\n出力先: {targetOutputFolder}"
                : $"{appliedCount} 件のマテリアルに非破壊でツヤを適用しました。\n出力先: {targetOutputFolder}";
            statusMessageType = MessageType.Info;
        }

        private void RevertTargetsToOriginal()
        {
            previewRenderer.ClearScenePreview();
            liveScenePreview = false;

            if (targetRoot == null)
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Revert Hair Gloss Studio");
            int restored = 0;
            try
            {
                foreach (Renderer renderer in targetRoot.GetComponentsInChildren<Renderer>(true))
                {
                    for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                    {
                        HairGlossBackupStore store = HairGlossBackupStore.FindForTarget(renderer, i);
                        if (store != null && store.RestoreTarget(renderer, i)) restored++;
                    }
                }
            }
            finally { Undo.CollapseUndoOperations(undoGroup); }

            LoadTargets();
            statusMessage = restored > 0
                ? $"{restored} 件のマテリアルを元の状態に戻しました。"
                : "プレビューを解除し、元のマテリアル状態を確認しました。";
            statusMessageType = MessageType.Info;
        }

        private HairGlossBackupStore GetBackupStoreIfExists()
        {
            if (targetRoot != null)
            {
                foreach (Renderer renderer in targetRoot.GetComponentsInChildren<Renderer>(true))
                {
                    for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                    {
                        HairGlossBackupStore store = HairGlossBackupStore.FindForTarget(renderer, i);
                        if (store != null) return store;
                    }
                }
            }
            backupStore = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(HairGlossBackupStore.DefaultStoreAssetPath);
            return backupStore;
        }
    }
}
