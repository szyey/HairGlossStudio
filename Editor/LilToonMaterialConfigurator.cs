using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    /// <summary>
    /// Configures lilToon material properties for hair MatCap (1st or 2nd),
    /// Anisotropy, Reflection, and 2nd Normal Map, and synchronizes keywords via reflection.
    /// </summary>
    public static class LilToonMaterialConfigurator
    {
        public static bool IsMatCap1stActive(Material mat)
        {
            if (mat == null) return false;
            return mat.HasProperty("_UseMatCap") && mat.GetFloat("_UseMatCap") > 0.5f;
        }

        public static void ConfigureMaterial(
            Material mat,
            HairGlossSettings settings,
            Texture2D flowNormal,
            Texture2D matCapTex,
            Texture2D maskTex,
            Texture2D tangentMap)
        {
            if (mat == null || settings == null)
            {
                return;
            }

            // 1. MatCap Configuration (1st or 2nd slot)
            bool use2nd = settings.matCapSlot == MatCapSlotMode.MatCap2nd;
            if (!use2nd)
            {
                ConfigureMatCap1st(mat, settings, flowNormal, matCapTex, maskTex);
            }
            else
            {
                ConfigureMatCap2nd(mat, settings, flowNormal, matCapTex, maskTex);
            }

            // 2. Anisotropy Configuration
            SetFloatSafe(mat, "_UseAnisotropy", settings.enableAnisotropy ? 1f : 0f);
            SetTextureSafe(mat, "_AnisotropyTangentMap", tangentMap);
            SetFloatSafe(mat, "_AnisotropyScale", Mathf.Clamp01(settings.glossIntensity));
            SetTextureSafe(mat, "_AnisotropyScaleMask", maskTex);
            SetFloatSafe(mat, "_AnisotropyTangentWidth", settings.anisotropyTangentWidth);
            SetFloatSafe(mat, "_AnisotropyBitangentWidth", settings.anisotropyBitangentWidth);
            SetFloatSafe(mat, "_AnisotropyShift", settings.anisotropyShift);
            SetFloatSafe(mat, "_AnisotropyShiftNoiseScale", settings.anisotropyShiftNoiseScale);
            SetFloatSafe(mat, "_AnisotropySpecularStrength", settings.anisotropySpecularStrength);
            SetFloatSafe(mat, "_Anisotropy2ndSpecularStrength", 0.5f);
            SetFloatSafe(mat, "_AnisotropyMatCapStrength", settings.anisotropyMatCapStrength);
            SetFloatSafe(mat, "_Anisotropy2ndMatCapStrength", settings.anisotropyMatCapStrength);

            // 3. Reflection / Specular Configuration
            if (settings.enableAnisotropy)
            {
                SetFloatSafe(mat, "_UseReflection", 1f);
                SetFloatSafe(mat, "_Smoothness", settings.smoothness);
                SetColorSafe(
                    mat,
                    "_ReflectionColor",
                    new Color(
                        settings.glossColor.r,
                        settings.glossColor.g,
                        settings.glossColor.b,
                        Mathf.Clamp01(settings.glossIntensity * 0.65f)));
                SetFloatSafe(mat, "_ReflectionBlendMode", 1f);
            }

            // 4. Normal Map 2nd Configuration (only if not already occupied by another detail normal)
            bool hasExistingBump2nd = mat.HasProperty("_UseBump2ndMap")
                                      && mat.GetFloat("_UseBump2ndMap") > 0.5f
                                      && mat.HasProperty("_Bump2ndMap")
                                      && mat.GetTexture("_Bump2ndMap") != null
                                      && (flowNormal == null || mat.GetTexture("_Bump2ndMap") != flowNormal);

            if (!hasExistingBump2nd)
            {
                SetFloatSafe(mat, "_UseBump2ndMap", settings.strandBumpScale > 0.001f ? 1f : 0f);
                SetTextureSafe(mat, "_Bump2ndMap", flowNormal);
                SetFloatSafe(mat, "_Bump2ndScale", settings.strandBumpScale * 0.5f);
                SetTextureSafe(mat, "_Bump2ndScaleMask", maskTex);
            }

            // 5. Synchronize shader keywords and lilToon Multi variants
            SyncShaderKeywords(mat, settings);
            EditorUtility.SetDirty(mat);
        }

        private static void ConfigureMatCap1st(
            Material mat,
            HairGlossSettings settings,
            Texture2D flowNormal,
            Texture2D matCapTex,
            Texture2D maskTex)
        {
            float hdrBoost = settings.glossIntensity > 1f ? settings.glossIntensity : 1f;
            Color matCapColor = new Color(hdrBoost, hdrBoost, hdrBoost, 1f);

            SetFloatSafe(mat, "_UseMatCap", 1f);
            SetTextureSafe(mat, "_MatCapTex", matCapTex);
            SetColorSafe(mat, "_MatCapColor", matCapColor);
            SetVectorSafe(mat, "_MatCapBlendUV1", settings.matCapBlendUV1);
            SetFloatSafe(mat, "_MatCapZRotCancel", 1f);
            SetFloatSafe(mat, "_MatCapPerspective", 1f);
            SetFloatSafe(mat, "_MatCapVRParallaxStrength", 0.35f);
            SetFloatSafe(mat, "_MatCapBlend", Mathf.Clamp01(settings.glossIntensity));
            SetTextureSafe(mat, "_MatCapBlendMask", maskTex);
            SetFloatSafe(mat, "_MatCapEnableLighting", 1f);
            SetFloatSafe(mat, "_MatCapShadowMask", settings.shadowMaskStrength);
            SetFloatSafe(mat, "_MatCapBackfaceMask", 1f);
            SetFloatSafe(mat, "_MatCapLod", Mathf.Lerp(4f, 0f, settings.smoothness));
            SetFloatSafe(mat, "_MatCapBlendMode", settings.matCapBlendMode);
            SetFloatSafe(mat, "_MatCapNormalStrength", settings.matCapNormalStrength);
            SetFloatSafe(mat, "_MatCapCustomNormal", settings.strandBumpScale > 0.001f ? 1f : 0f);
            SetTextureSafe(mat, "_MatCapBumpMap", flowNormal);
            SetFloatSafe(mat, "_MatCapBumpScale", settings.strandBumpScale);
        }

        private static void ConfigureMatCap2nd(
            Material mat,
            HairGlossSettings settings,
            Texture2D flowNormal,
            Texture2D matCapTex,
            Texture2D maskTex)
        {
            float hdrBoost = settings.glossIntensity > 1f ? settings.glossIntensity : 1f;
            Color matCapColor = new Color(hdrBoost, hdrBoost, hdrBoost, 1f);

            SetFloatSafe(mat, "_UseMatCap2nd", 1f);
            SetTextureSafe(mat, "_MatCap2ndTex", matCapTex);
            SetColorSafe(mat, "_MatCap2ndColor", matCapColor);
            SetVectorSafe(mat, "_MatCap2ndBlendUV1", settings.matCapBlendUV1);
            SetFloatSafe(mat, "_MatCap2ndZRotCancel", 1f);
            SetFloatSafe(mat, "_MatCap2ndPerspective", 1f);
            SetFloatSafe(mat, "_MatCap2ndVRParallaxStrength", 0.35f);
            SetFloatSafe(mat, "_MatCap2ndBlend", Mathf.Clamp01(settings.glossIntensity));
            SetTextureSafe(mat, "_MatCap2ndBlendMask", maskTex);
            SetFloatSafe(mat, "_MatCap2ndEnableLighting", 1f);
            SetFloatSafe(mat, "_MatCap2ndShadowMask", settings.shadowMaskStrength);
            SetFloatSafe(mat, "_MatCap2ndBackfaceMask", 1f);
            SetFloatSafe(mat, "_MatCap2ndLod", Mathf.Lerp(4f, 0f, settings.smoothness));
            SetFloatSafe(mat, "_MatCap2ndBlendMode", settings.matCapBlendMode);
            SetFloatSafe(mat, "_MatCap2ndNormalStrength", settings.matCapNormalStrength);
            SetFloatSafe(mat, "_MatCap2ndCustomNormal", settings.strandBumpScale > 0.001f ? 1f : 0f);
            SetTextureSafe(mat, "_MatCap2ndBumpMap", flowNormal);
            SetFloatSafe(mat, "_MatCap2ndBumpScale", settings.strandBumpScale);
        }

        private static void SyncShaderKeywords(Material mat, HairGlossSettings settings)
        {
            if (mat.shader != null && mat.shader.name.IndexOf("Multi", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (InvokeLilToonSetupReflection(mat)) return;
                SetKeyword(mat, "_NORMALMAP", IsFeatureEnabled(mat, "_UseBumpMap"));
                SetKeyword(mat, "SOURCE_GBUFFER", IsFeatureEnabled(mat, "_UseAnisotropy"));
                SetKeyword(mat, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", IsFeatureEnabled(mat, "_UseMatCap"));
                SetKeyword(mat, "_SPECULARHIGHLIGHTS_OFF", IsFeatureEnabled(mat, "_UseMatCap2nd"));
                SetKeyword(mat, "EFFECT_BUMP", IsFeatureEnabled(mat, "_UseBump2ndMap"));
                SetKeyword(mat, "GEOM_TYPE_MESH",
                    (IsFeatureEnabled(mat, "_UseMatCap") && IsFeatureEnabled(mat, "_MatCapCustomNormal")) ||
                    (IsFeatureEnabled(mat, "_UseMatCap2nd") && IsFeatureEnabled(mat, "_MatCap2ndCustomNormal")));
            }
        }

        private static bool IsFeatureEnabled(Material mat, string propertyName)
        {
            return mat.HasProperty(propertyName) && mat.GetFloat(propertyName) > 0.5f;
        }

        private static void SetKeyword(Material mat, string keyword, bool enabled)
        {
            if (enabled) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        private static bool InvokeLilToonSetupReflection(Material mat)
        {
            try
            {
                string[] candidateMethods = { "SetupMultiMaterial" };
                Type[] matArgTypes = { typeof(Material) };
                object[] args = { mat };

                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly asm = assemblies[i];
                    Type[] candidateTypes =
                    {
                        asm.GetType("lilToon.lilMaterialUtils") ?? asm.GetType("lilMaterialUtils"),
                        asm.GetType("lilToon.lilToonInspector") ?? asm.GetType("lilToonInspector")
                    };

                    for (int t = 0; t < candidateTypes.Length; t++)
                    {
                        Type type = candidateTypes[t];
                        if (type == null) continue;

                        for (int m = 0; m < candidateMethods.Length; m++)
                        {
                            MethodInfo method = type.GetMethod(
                                candidateMethods[m],
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                null,
                                matArgTypes,
                                null);
                            if (method != null)
                            {
                                method.Invoke(null, args);
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall back to the supported Multi keyword mapping.
            }
            return false;
        }

        private static void SetFloatSafe(Material mat, string propertyName, float value)
        {
            if (mat.HasProperty(propertyName))
            {
                mat.SetFloat(propertyName, value);
            }
        }

        private static void SetColorSafe(Material mat, string propertyName, Color value)
        {
            if (mat.HasProperty(propertyName))
            {
                mat.SetColor(propertyName, value);
            }
        }

        private static void SetVectorSafe(Material mat, string propertyName, Vector4 value)
        {
            if (mat.HasProperty(propertyName))
            {
                mat.SetVector(propertyName, value);
            }
        }

        private static void SetTextureSafe(Material mat, string propertyName, Texture value)
        {
            if (mat.HasProperty(propertyName))
            {
                mat.SetTexture(propertyName, value);
            }
        }
    }
}
