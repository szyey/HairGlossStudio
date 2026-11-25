using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_2021_2_OR_NEWER
using PrefabStage = UnityEditor.SceneManagement.PrefabStage;
#else
using PrefabStage = UnityEditor.Experimental.SceneManagement.PrefabStage;
#endif

namespace HairGlossStudio.Editor
{
    /// <summary>
    /// Renders a 2D synthetic hair dome preview inside the EditorWindow and manages
    /// transient non-saved materials for live SceneView previewing.
    /// </summary>
    public class HairGlossPreviewRenderer : IDisposable
    {
        private class ScenePreviewEntry
        {
            public Renderer renderer;
            public int submeshIndex;
            public Material restoreMaterial;
            public Material transientMaterial;
        }

        private class PreviewTextureSet : IDisposable
        {
            public Texture2D rawNormal;
            public Texture2D rawTangent;
            public Texture2D normal;
            public Texture2D tangent;
            public Texture2D matCap;
            public Texture2D mask;
            public Color baseColor;
            public string normalPath;
            public string tangentPath;

            public void Dispose()
            {
                if (rawNormal != null) UnityEngine.Object.DestroyImmediate(rawNormal);
                if (rawTangent != null) UnityEngine.Object.DestroyImmediate(rawTangent);
                if (matCap != null) UnityEngine.Object.DestroyImmediate(matCap);
                if (mask != null) UnityEngine.Object.DestroyImmediate(mask);
                if (!string.IsNullOrEmpty(normalPath)) AssetDatabase.DeleteAsset(normalPath);
                if (!string.IsNullOrEmpty(tangentPath)) AssetDatabase.DeleteAsset(tangentPath);
            }
        }

        private const int PreviewResolution = 256;

        private readonly List<ScenePreviewEntry> activeScenePreviews = new List<ScenePreviewEntry>();

        private readonly HairFlowAnalyzer.AnalysisCache analysisCache = new HairFlowAnalyzer.AnalysisCache();
        private readonly Dictionary<string, PreviewTextureSet> textureCache = new Dictionary<string, PreviewTextureSet>();
        private PreviewTextureSet referenceTextures;
        private string cachedSettings;
        private float cachedFlowWeight = float.NaN;
        private string previewFolder;
        private List<HairMaterialTarget> lastTargets;
        private HairGlossSettings lastSettings;
        private bool resumeAfterSave;
        private bool disposed;
        private Texture2D domePreviewTex;

        public Texture2D NormalPreview => referenceTextures?.rawNormal;
        public Texture2D MatCapPreview => referenceTextures?.matCap;
        public Texture2D MaskPreview => referenceTextures?.mask;
        public Texture2D DomePreview => domePreviewTex;
        public bool HasActiveScenePreview => activeScenePreviews.Count > 0;

        public HairGlossPreviewRenderer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            PrefabStage.prefabSaving += OnPrefabSaving;
            PrefabStage.prefabSaved += OnPrefabSaved;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            PrefabStage.prefabSaving -= OnPrefabSaving;
            PrefabStage.prefabSaved -= OnPrefabSaved;
            EditorApplication.delayCall -= ResumeScenePreview;
            ClearScenePreview();
            DestroyPreviewTextures();
            if (!string.IsNullOrEmpty(previewFolder)) AssetDatabase.DeleteAsset(previewFolder);
        }

        public void InvalidateAnalysisCache()
        {
            analysisCache.Clear();
            DestroyPreviewTextures();
        }

        public void RebuildPreviews(HairMaterialTarget referenceTarget, HairGlossSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            Material baseMat = referenceTarget != null
                ? (referenceTarget.originalMaterial != null ? referenceTarget.originalMaterial : referenceTarget.currentMaterial)
                : null;
            referenceTextures = GetTextureSet(referenceTarget, baseMat, settings);
            if (domePreviewTex != null) UnityEngine.Object.DestroyImmediate(domePreviewTex);
            domePreviewTex = RenderSyntheticHairDome(settings, referenceTextures.baseColor, referenceTextures.rawNormal, referenceTextures.matCap);
            domePreviewTex.hideFlags = HideFlags.HideAndDontSave;
        }

        private PreviewTextureSet GetTextureSet(HairMaterialTarget target, Material baseMat, HairGlossSettings settings)
        {
            string settingsJson = JsonUtility.ToJson(settings);
            if (cachedSettings != settingsJson)
            {
                DestroyPreviewTextures();
                cachedSettings = settingsJson;
            }
            Mesh mesh = target != null ? target.mesh : null;
            int submesh = target != null ? target.submeshIndex : 0;
            Texture2D mainTex = null;
            Color baseColor = new Color(0.28f, 0.22f, 0.20f, 1f);

            if (baseMat != null)
            {
                if (baseMat.HasProperty("_MainTex"))
                {
                    mainTex = baseMat.GetTexture("_MainTex") as Texture2D;
                }
                if (baseMat.HasProperty("_Color"))
                {
                    baseColor = baseMat.GetColor("_Color");
                }
            }

            float flowWeight = settings.autoAnalyzeFlow ? settings.flowAnalyzeWeight : 0f;
            if (cachedFlowWeight != flowWeight)
            {
                analysisCache.Clear();
                cachedFlowWeight = flowWeight;
            }
            string analysisKey = HairFlowAnalyzer.GetAnalysisCacheKey(mesh, submesh, mainTex, PreviewResolution, flowWeight);
            string textureKey = $"{analysisKey}:{baseColor.r:R}:{baseColor.g:R}:{baseColor.b:R}";
            if (textureCache.TryGetValue(textureKey, out PreviewTextureSet cached)) return cached;
            HairFlowAnalyzer.AnalysisResult analysis = analysisCache.GetOrAnalyze(mesh, submesh, mainTex, PreviewResolution, flowWeight);
            var textures = new PreviewTextureSet();
            try
            {
                textures.baseColor = new Color(analysis.averageBaseColor.r * baseColor.r,
                    analysis.averageBaseColor.g * baseColor.g, analysis.averageBaseColor.b * baseColor.b, 1f);
                textures.rawNormal = HairTextureGenerator.GenerateFlowNormalMap(analysis, settings);
                textures.rawNormal.hideFlags = HideFlags.HideAndDontSave;
                textures.matCap = HairTextureGenerator.GenerateGlossMatCap(settings, textures.baseColor, PreviewResolution);
                textures.matCap.hideFlags = HideFlags.HideAndDontSave;
                textures.mask = HairTextureGenerator.GenerateGlossMask(analysis, settings);
                textures.mask.hideFlags = HideFlags.HideAndDontSave;
                textures.rawTangent = HairTextureGenerator.GenerateAnisotropyTangentMap(analysis);
                textures.rawTangent.hideFlags = HideFlags.HideAndDontSave;
                textureCache.Add(textureKey, textures);
                return textures;
            }
            catch
            {
                textures.Dispose();
                throw;
            }
        }

        private void ImportSceneNormals(PreviewTextureSet textures)
        {
            if (textures.normal != null && textures.tangent != null) return;
            if (string.IsNullOrEmpty(previewFolder))
            {
                previewFolder = AssetDatabase.GenerateUniqueAssetPath("Assets/HairGlossStudio_Preview");
                HairTextureGenerator.EnsureFolderExists(previewFolder);
            }
            if (string.IsNullOrEmpty(textures.normalPath))
            {
                string id = Guid.NewGuid().ToString("N");
                textures.normalPath = $"{previewFolder}/{id}_Normal.png";
                textures.tangentPath = $"{previewFolder}/{id}_Tangent.png";
            }
            textures.normal = HairTextureGenerator.ImportPreviewNormal(textures.normalPath, textures.rawNormal);
            textures.tangent = HairTextureGenerator.ImportPreviewNormal(textures.tangentPath, textures.rawTangent);
        }

        public void UpdateScenePreview(
            List<HairMaterialTarget> targets,
            HairGlossSettings settings,
            HairGlossBackupStore backupStore)
        {
            if (disposed || targets == null || settings == null || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ClearScenePreview();
                return;
            }

            lastTargets = new List<HairMaterialTarget>(targets);
            lastSettings = settings.Clone();

            // Build lookup of currently active previewed slots
            var desiredKeys = new HashSet<string>();
            foreach (HairMaterialTarget t in targets)
            {
                if (t == null || !t.isSelected || !t.isLilToon || t.renderer == null)
                {
                    continue;
                }

                string key = MakeSlotKey(t.renderer, t.submeshIndex);
                desiredKeys.Add(key);

                ScenePreviewEntry existing = FindScenePreviewEntry(t.renderer, t.submeshIndex);
                Material sourceMat = ResolveSourceMaterial(t, backupStore, existing);
                if (sourceMat == null)
                {
                    continue;
                }
                PreviewTextureSet textures = GetTextureSet(t, sourceMat, settings);
                ImportSceneNormals(textures);

                if (existing == null)
                {
                    Material[] mats = t.renderer.sharedMaterials;
                    if (mats == null || t.submeshIndex < 0 || t.submeshIndex >= mats.Length)
                    {
                        continue;
                    }

                    Material transient = new Material(sourceMat)
                    {
                        name = $"{sourceMat.name}_HairGlossPreview",
                        hideFlags = HideFlags.DontSave
                    };

                    existing = new ScenePreviewEntry
                    {
                        renderer = t.renderer,
                        submeshIndex = t.submeshIndex,
                        restoreMaterial = mats[t.submeshIndex],
                        transientMaterial = transient
                    };
                    activeScenePreviews.Add(existing);

                    mats[t.submeshIndex] = transient;
                    t.renderer.sharedMaterials = mats;
                }
                else
                {
                    // Refresh transient properties from original base material
                    if (existing.transientMaterial.shader != sourceMat.shader)
                    {
                        existing.transientMaterial.shader = sourceMat.shader;
                    }
                    existing.transientMaterial.CopyPropertiesFromMaterial(sourceMat);
                    Material[] mats = t.renderer.sharedMaterials;
                    if (mats != null && t.submeshIndex >= 0 && t.submeshIndex < mats.Length &&
                        mats[t.submeshIndex] != existing.transientMaterial)
                    {
                        mats[t.submeshIndex] = existing.transientMaterial;
                        t.renderer.sharedMaterials = mats;
                    }
                }

                LilToonMaterialConfigurator.ConfigureMaterial(
                    existing.transientMaterial,
                    settings,
                    textures.normal,
                    textures.matCap,
                    textures.mask,
                    textures.tangent,
                    false);
            }

            // Revert any entries no longer selected
            for (int i = activeScenePreviews.Count - 1; i >= 0; i--)
            {
                ScenePreviewEntry entry = activeScenePreviews[i];
                if (entry.renderer == null || !desiredKeys.Contains(MakeSlotKey(entry.renderer, entry.submeshIndex)))
                {
                    RestoreEntry(entry);
                    activeScenePreviews.RemoveAt(i);
                }
            }

            SceneView.RepaintAll();
        }

        public void ClearScenePreview()
        {
            resumeAfterSave = false;
            EditorApplication.delayCall -= ResumeScenePreview;
            if (activeScenePreviews.Count == 0)
            {
                return;
            }

            for (int i = activeScenePreviews.Count - 1; i >= 0; i--)
            {
                RestoreEntry(activeScenePreviews[i]);
            }
            activeScenePreviews.Clear();
            SceneView.RepaintAll();
        }

        private void SuspendForSave()
        {
            bool wasActive = HasActiveScenePreview || resumeAfterSave;
            ClearScenePreview();
            resumeAfterSave = wasActive;
        }

        private void OnSceneSaving(Scene scene, string path) { SuspendForSave(); }
        private void OnPrefabSaving(GameObject root) { SuspendForSave(); }
        private void OnSceneSaved(Scene scene) { QueueResume(); }
        private void OnPrefabSaved(GameObject root) { QueueResume(); }
        private void OnSceneClosing(Scene scene, bool removingScene) { ClearScenePreview(); }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) ClearScenePreview();
        }

        private void QueueResume()
        {
            if (!resumeAfterSave) return;
            EditorApplication.delayCall -= ResumeScenePreview;
            EditorApplication.delayCall += ResumeScenePreview;
        }

        private void ResumeScenePreview()
        {
            if (!resumeAfterSave || disposed || EditorApplication.isPlayingOrWillChangePlaymode) return;
            resumeAfterSave = false;
            UpdateScenePreview(lastTargets, lastSettings, null);
        }

        public void DetachScenePreviewWithoutRestore()
        {
            for (int i = 0; i < activeScenePreviews.Count; i++)
            {
                if (activeScenePreviews[i].transientMaterial != null)
                {
                    UnityEngine.Object.DestroyImmediate(activeScenePreviews[i].transientMaterial);
                }
            }
            activeScenePreviews.Clear();
        }

        private static Material ResolveSourceMaterial(
            HairMaterialTarget target,
            HairGlossBackupStore backupStore,
            ScenePreviewEntry existing)
        {
            backupStore = HairGlossBackupStore.FindForTarget(target.renderer, target.submeshIndex);
            if (backupStore != null &&
                backupStore.TryGetRecord(target.renderer, target.submeshIndex, out RendererMaterialRecord rec) &&
                rec.originalMaterial != null)
            {
                return rec.originalMaterial;
            }

            if (existing != null && existing.restoreMaterial != null)
            {
                return existing.restoreMaterial;
            }

            return target.originalMaterial != null ? target.originalMaterial : target.currentMaterial;
        }

        private static void RestoreEntry(ScenePreviewEntry entry)
        {
            if (entry.renderer != null)
            {
                Material[] mats = entry.renderer.sharedMaterials;
                if (mats != null && entry.submeshIndex >= 0 && entry.submeshIndex < mats.Length &&
                    mats[entry.submeshIndex] == entry.transientMaterial)
                {
                    mats[entry.submeshIndex] = entry.restoreMaterial;
                    entry.renderer.sharedMaterials = mats;
                }
            }

            if (entry.transientMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(entry.transientMaterial);
            }
        }

        private ScenePreviewEntry FindScenePreviewEntry(Renderer renderer, int submeshIndex)
        {
            for (int i = 0; i < activeScenePreviews.Count; i++)
            {
                if (activeScenePreviews[i].renderer == renderer && activeScenePreviews[i].submeshIndex == submeshIndex)
                {
                    return activeScenePreviews[i];
                }
            }
            return null;
        }

        private static string MakeSlotKey(Renderer renderer, int submesh)
        {
            return $"{renderer.GetInstanceID()}:{submesh}";
        }

        private static HairMaterialTarget FindFirstSelectedLilToon(List<HairMaterialTarget> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null && targets[i].isSelected && targets[i].isLilToon)
                {
                    return targets[i];
                }
            }
            return targets.Count > 0 ? targets[0] : null;
        }

        private static Texture2D RenderSyntheticHairDome(
            HairGlossSettings settings,
            Color baseHairColor,
            Texture2D normalMap,
            Texture2D matCapTex)
        {
            const int size = PreviewResolution;
            Color[] pixels = new Color[size * size];

            Color[] normalPixels = normalMap != null ? normalMap.GetPixels() : null;
            int nw = normalMap != null ? normalMap.width : 0;
            int nh = normalMap != null ? normalMap.height : 0;

            Color[] matCapPixels = matCapTex != null ? matCapTex.GetPixels() : null;
            int mw = matCapTex != null ? matCapTex.width : 0;
            int mh = matCapTex != null ? matCapTex.height : 0;

            Vector3 lightDir = new Vector3(-0.32f, 0.62f, 0.71f).normalized;
            Vector3 viewDir = Vector3.forward;
            Color bgTop = new Color(0.14f, 0.15f, 0.18f, 1f);
            Color bgBottom = new Color(0.09f, 0.10f, 0.12f, 1f);

            // Ensure base hair has visible saturation/brightness in preview even if white material
            Color hairAlbedo = baseHairColor;
            if (hairAlbedo.r > 0.96f && hairAlbedo.g > 0.96f && hairAlbedo.b > 0.96f)
            {
                hairAlbedo = new Color(0.36f, 0.27f, 0.25f, 1f);
            }

            float bumpAmp = settings.strandBumpScale * 0.45f;

            for (int py = 0; py < size; py++)
            {
                float vCoord = (py + 0.5f) / size;
                float sy = vCoord * 2f - 1f;
                Color bgColor = Color.Lerp(bgBottom, bgTop, vCoord);
                int row = py * size;

                for (int px = 0; px < size; px++)
                {
                    float uCoord = (px + 0.5f) / size;
                    float sx = uCoord * 2f - 1f;
                    float r2 = sx * sx + sy * sy;

                    if (r2 > 0.92f * 0.92f)
                    {
                        pixels[row + px] = bgColor;
                        continue;
                    }

                    // Normalize inside sphere radius 0.92
                    float nx0 = sx / 0.92f;
                    float ny0 = sy / 0.92f;
                    float nz0 = Mathf.Sqrt(Mathf.Max(0f, 1f - nx0 * nx0 - ny0 * ny0));

                    Vector3 normal = new Vector3(nx0, ny0, nz0);
                    Vector3 tangent = new Vector3(nz0, 0f, -nx0).normalized;
                    Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;

                    // Sample flow normal map using spherical mapping
                    float u = 0.5f + Mathf.Atan2(nx0, nz0) / Mathf.PI * 0.65f;
                    float v = 0.5f + Mathf.Asin(Mathf.Clamp(ny0, -1f, 1f)) / Mathf.PI;

                    if (normalPixels != null && nw > 0 && nh > 0)
                    {
                        int ix = Mathf.Clamp(Mathf.FloorToInt(u * nw), 0, nw - 1);
                        int iy = Mathf.Clamp(Mathf.FloorToInt(v * nh), 0, nh - 1);
                        Color nc = normalPixels[iy * nw + ix];
                        float tx = (nc.r * 2f - 1f) * bumpAmp;
                        float ty = (nc.g * 2f - 1f) * bumpAmp;
                        normal = (normal + tangent * tx + bitangent * ty).normalized;
                    }

                    // Toon shading base
                    float ndl = Vector3.Dot(normal, lightDir);
                    float shadowFactor = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.15f, 0.25f, ndl));
                    Color shadedBase = Color.Lerp(hairAlbedo * 0.52f, hairAlbedo, shadowFactor);

                    // Procedural strand variation on base hair
                    float strandShade = 0.92f + 0.08f * Mathf.Sin(u * 160f);
                    shadedBase *= strandShade;

                    // MatCap lookup
                    Color matCapSample = Color.black;
                    if (matCapPixels != null && mw > 0 && mh > 0)
                    {
                        float mu = Mathf.Clamp01(normal.x * 0.5f + 0.5f);
                        float mv = Mathf.Clamp01(normal.y * 0.5f + 0.5f);
                        int mx = Mathf.Clamp(Mathf.FloorToInt(mu * mw), 0, mw - 1);
                        int my = Mathf.Clamp(Mathf.FloorToInt(mv * mh), 0, mh - 1);
                        matCapSample = matCapPixels[my * mw + mx];
                    }

                    // Shadow mask attenuation
                    float matCapMask = Mathf.Lerp(1f, shadowFactor, settings.shadowMaskStrength);
                    float rootTip = Mathf.Clamp01(1f - v);
                    float rtMask = (settings.rootFalloff > 0.001f
                                       ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(rootTip / settings.rootFalloff))
                                       : 1f)
                                   * (settings.tipFalloff > 0.001f
                                       ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - rootTip) / settings.tipFalloff))
                                       : 1f);

                    Color mcTerm = matCapSample * (matCapMask * rtMask * settings.glossIntensity);

                    Color combined = shadedBase;
                    switch (settings.matCapBlendMode)
                    {
                        case 0: // Normal
                            combined = Color.Lerp(shadedBase, mcTerm, Mathf.Clamp01(mcTerm.maxColorComponent));
                            break;
                        case 1: // Add
                            combined = shadedBase + mcTerm;
                            break;
                        case 2: // Screen
                            combined = new Color(
                                1f - (1f - shadedBase.r) * (1f - Mathf.Clamp01(mcTerm.r)),
                                1f - (1f - shadedBase.g) * (1f - Mathf.Clamp01(mcTerm.g)),
                                1f - (1f - shadedBase.b) * (1f - Mathf.Clamp01(mcTerm.b)),
                                1f);
                            break;
                        case 3: // Multiply
                            combined = shadedBase * mcTerm;
                            break;
                    }

                    // Kajiya-Kay anisotropic highlight approximation
                    if (settings.enableAnisotropy)
                    {
                        Vector3 halfVec = (lightDir + viewDir).normalized;
                        Vector3 shiftedBitangent = (bitangent + normal * (settings.anisotropyShift * 0.45f)).normalized;
                        float dotBH = Vector3.Dot(shiftedBitangent, halfVec);
                        float sinBH = Mathf.Sqrt(Mathf.Max(0f, 1f - dotBH * dotBH));
                        float exp = Mathf.Lerp(14f, 95f, settings.smoothness) / Mathf.Max(0.25f, settings.highlightWidth * 2f);
                        float anisoSpec = Mathf.Pow(sinBH, exp) * settings.glossIntensity * 0.38f * rtMask * shadowFactor;
                        combined += settings.glossColor * anisoSpec;
                    }

                    // Anti-aliased outer sphere edge
                    float r = Mathf.Sqrt(r2);
                    float alphaEdge = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.89f, 0.92f, r));
                    pixels[row + px] = Color.Lerp(bgColor, new Color(
                        Mathf.Clamp01(combined.r),
                        Mathf.Clamp01(combined.g),
                        Mathf.Clamp01(combined.b),
                        1f), alphaEdge);
                }
            }

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            try
            {
                tex.SetPixels(pixels);
                tex.Apply(false, false);
                return tex;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(tex);
                throw;
            }
        }

        private void DestroyPreviewTextures()
        {
            foreach (PreviewTextureSet textures in textureCache.Values) textures.Dispose();
            textureCache.Clear();
            referenceTextures = null;
            if (domePreviewTex != null) UnityEngine.Object.DestroyImmediate(domePreviewTex);
            domePreviewTex = null;
        }
    }
}
