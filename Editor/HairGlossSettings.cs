using System;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    public enum GlossIntensityPreset
    {
        Weak,
        Medium,
        Strong,
        Custom
    }

    public enum GlossFinishPreset
    {
        Gloss,
        Matte,
        Custom
    }

    public enum GlossWidthPreset
    {
        Thin,
        Normal,
        Wide,
        Custom
    }

    public enum StrandBumpPreset
    {
        None,
        Subtle,
        Crisp,
        Custom
    }

    public enum MatCapSlotMode
    {
        MatCap1st,
        MatCap2nd
    }

    /// <summary>
    /// Holds all user-configurable parameters and preset helpers for Hair Gloss Studio.
    /// </summary>
    [Serializable]
    public class HairGlossSettings
    {
        [Header("Presets")]
        public GlossIntensityPreset intensityPreset = GlossIntensityPreset.Medium;
        public GlossFinishPreset finishPreset = GlossFinishPreset.Gloss;
        public GlossWidthPreset widthPreset = GlossWidthPreset.Normal;
        public StrandBumpPreset strandPreset = StrandBumpPreset.Subtle;

        [Header("Core Parameters")]
        [Range(0f, 2f)]
        public float glossIntensity = 0.85f;

        [Range(0f, 1f)]
        public float smoothness = 0.88f;

        [Range(0.05f, 1f)]
        public float highlightWidth = 0.35f;

        public Color glossColor = new Color(1f, 1f, 1f, 1f);

        [Range(0f, 1f)]
        public float mainTexColorBlend = 0.15f;

        [Header("Angel Ring & MatCap")]
        public bool enableAngelRing = true;

        [Range(-0.5f, 0.8f)]
        public float angelRingHeight = 0.22f;

        [Range(0f, 1f)]
        public float angelRingArch = 0.45f;

        [Range(0.1f, 1f)]
        public float angelRingSharpness = 0.7f;

        public Vector4 matCapBlendUV1 = new Vector4(0f, 0.35f, 0f, 0f);

        [Range(0f, 2f)]
        public float matCapNormalStrength = 1.0f;

        public MatCapSlotMode matCapSlot = MatCapSlotMode.MatCap1st;

        // 0 = Normal, 1 = Add, 2 = Screen, 3 = Multiply
        public int matCapBlendMode = 1;

        [Header("Strand Flow & Normal Map")]
        public bool autoAnalyzeFlow = true;

        [Range(10f, 200f)]
        public float strandFrequency = 75f;

        [Range(0f, 2f)]
        public float strandBumpScale = 0.45f;

        [Range(0f, 1f)]
        public float flowAnalyzeWeight = 0.7f;

        [Header("Anisotropy")]
        public bool enableAnisotropy = true;

        [Range(0.05f, 2f)]
        public float anisotropyTangentWidth = 0.8f;

        [Range(0.02f, 1f)]
        public float anisotropyBitangentWidth = 0.15f;

        [Range(-1f, 1f)]
        public float anisotropyShift = -0.1f;

        [Range(0f, 1f)]
        public float anisotropyShiftNoiseScale = 0.15f;

        [Range(0f, 2f)]
        public float anisotropySpecularStrength = 1.0f;

        [Range(0f, 2f)]
        public float anisotropyMatCapStrength = 0.85f;

        [Header("Mask & Falloff")]
        [Range(0f, 0.5f)]
        public float rootFalloff = 0.15f;

        [Range(0f, 0.5f)]
        public float tipFalloff = 0.12f;

        [Range(0f, 1f)]
        public float shadowMaskStrength = 0.65f;

        public int textureResolution = 1024;

        public string outputFolder = "Assets/HairGlossStudio_Generated";

        public void ApplyIntensityPreset(GlossIntensityPreset preset)
        {
            intensityPreset = preset;
            switch (preset)
            {
                case GlossIntensityPreset.Weak:
                    glossIntensity = 0.45f;
                    anisotropySpecularStrength = 0.6f;
                    anisotropyMatCapStrength = 0.5f;
                    break;
                case GlossIntensityPreset.Medium:
                    glossIntensity = 0.85f;
                    anisotropySpecularStrength = 1.0f;
                    anisotropyMatCapStrength = 0.85f;
                    break;
                case GlossIntensityPreset.Strong:
                    glossIntensity = 1.35f;
                    anisotropySpecularStrength = 1.45f;
                    anisotropyMatCapStrength = 1.25f;
                    break;
            }
        }

        public void ApplyFinishPreset(GlossFinishPreset preset)
        {
            finishPreset = preset;
            switch (preset)
            {
                case GlossFinishPreset.Gloss:
                    smoothness = 0.88f;
                    angelRingSharpness = 0.78f;
                    matCapBlendMode = 1; // Add
                    break;
                case GlossFinishPreset.Matte:
                    smoothness = 0.42f;
                    angelRingSharpness = 0.35f;
                    matCapBlendMode = 2; // Screen
                    break;
            }
        }

        public void ApplyWidthPreset(GlossWidthPreset preset)
        {
            widthPreset = preset;
            switch (preset)
            {
                case GlossWidthPreset.Thin:
                    highlightWidth = 0.18f;
                    anisotropyBitangentWidth = 0.08f;
                    break;
                case GlossWidthPreset.Normal:
                    highlightWidth = 0.35f;
                    anisotropyBitangentWidth = 0.15f;
                    break;
                case GlossWidthPreset.Wide:
                    highlightWidth = 0.60f;
                    anisotropyBitangentWidth = 0.32f;
                    break;
            }
        }

        public void ApplyStrandPreset(StrandBumpPreset preset)
        {
            strandPreset = preset;
            switch (preset)
            {
                case StrandBumpPreset.None:
                    strandBumpScale = 0f;
                    break;
                case StrandBumpPreset.Subtle:
                    strandBumpScale = 0.45f;
                    strandFrequency = 75f;
                    break;
                case StrandBumpPreset.Crisp:
                    strandBumpScale = 1.05f;
                    strandFrequency = 110f;
                    break;
            }
        }

        public HairGlossSettings Clone()
        {
            return (HairGlossSettings)MemberwiseClone();
        }
    }
}
