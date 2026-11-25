using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    /// <summary>
    /// Generates hair flow normal maps, Angel Ring MatCap textures, and gloss masks
    /// from flow analysis results and HairGlossSettings.
    /// </summary>
    public static class HairTextureGenerator
    {
        public class SavedTextureSet
        {
            public Texture2D normalMap;
            public Texture2D matCap;
            public Texture2D mask;
            public Texture2D tangentMap;
            public string normalMapPath;
            public string matCapPath;
            public string maskPath;
            public string tangentMapPath;
        }

        public static Texture2D GenerateAnisotropyTangentMap(HairFlowAnalyzer.AnalysisResult analysis)
        {
            if (analysis == null || analysis.width <= 0 || analysis.height <= 0 ||
                analysis.flowField == null || analysis.flowField.Length != analysis.width * analysis.height)
                throw new ArgumentException("A complete hair flow field is required.", nameof(analysis));

            Color[] pixels = new Color[analysis.flowField.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Vector2 flow = analysis.flowField[i];
                flow = flow.sqrMagnitude > 1e-6f ? flow.normalized : Vector2.down;
                pixels[i] = new Color(flow.x * 0.5f + 0.5f, flow.y * 0.5f + 0.5f, 0.5f, 1f);
            }
            return CreateTexture(analysis.width, analysis.height, pixels, true, "HairAnisotropyTangent", TextureWrapMode.Repeat);
        }

        private static Texture2D CreateTexture(int width, int height, Color[] pixels, bool linear, string name, TextureWrapMode wrapMode)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
            try
            {
                texture.name = name;
                texture.wrapMode = wrapMode;
                texture.SetPixels(pixels);
                texture.Apply(true, false);
                return texture;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        public static Texture2D GenerateFlowNormalMap(
            HairFlowAnalyzer.AnalysisResult analysis,
            HairGlossSettings settings)
        {
            int w = analysis.width;
            int h = analysis.height;
            int count = w * h;

            Color[] pixels = new Color[count];
            if (settings.strandBumpScale <= 0.001f)
            {
                Color flatNormal = new Color(0.5f, 0.5f, 1f, 1f);
                for (int i = 0; i < count; i++)
                {
                    pixels[i] = flatNormal;
                }
                return CreateTexture(w, h, pixels, true, "HairFlowNormal_Preview", TextureWrapMode.Repeat);
            }

            // Build deterministic multi-octave noise buffer
            float[] noiseField = new float[count];
            float coarseFreq = Mathf.Max(8f, settings.strandFrequency * 0.55f);
            float fineFreq = Mathf.Max(16f, settings.strandFrequency * 1.85f);

            for (int y = 0; y < h; y++)
            {
                float v = (float)y / h;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / w;
                    float n1 = ValueNoise(u * coarseFreq, v * coarseFreq, 17);
                    float n2 = ValueNoise(u * fineFreq, v * fineFreq, 93);
                    noiseField[row + x] = 0.6f * n1 + 0.4f * n2;
                }
            }

            // Line Integral Convolution (LIC) along the analyzed flow vector field
            float[] heightField = new float[count];
            const int halfSteps = 8;
            const float stepPx = 1.25f;

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = row + x;
                    float sum = noiseField[idx];
                    int samples = 1;

                    // Trace forward along flow
                    float fx = x + 0.5f;
                    float fy = y + 0.5f;
                    for (int s = 0; s < halfSteps; s++)
                    {
                        int ix = Mathf.Clamp((int)fx, 0, w - 1);
                        int iy = Mathf.Clamp((int)fy, 0, h - 1);
                        Vector2 dir = analysis.flowField[iy * w + ix];
                        fx += dir.x * stepPx;
                        fy += dir.y * stepPx;

                        int sx = Mathf.Clamp((int)fx, 0, w - 1);
                        int sy = Mathf.Clamp((int)fy, 0, h - 1);
                        sum += noiseField[sy * w + sx];
                        samples++;
                    }

                    // Trace backward along flow
                    fx = x + 0.5f;
                    fy = y + 0.5f;
                    for (int s = 0; s < halfSteps; s++)
                    {
                        int ix = Mathf.Clamp((int)fx, 0, w - 1);
                        int iy = Mathf.Clamp((int)fy, 0, h - 1);
                        Vector2 dir = analysis.flowField[iy * w + ix];
                        fx -= dir.x * stepPx;
                        fy -= dir.y * stepPx;

                        int sx = Mathf.Clamp((int)fx, 0, w - 1);
                        int sy = Mathf.Clamp((int)fy, 0, h - 1);
                        sum += noiseField[sy * w + sx];
                        samples++;
                    }

                    float licVal = sum / samples;

                    // Fold in slight texture luminance relief so painted strands align with normals
                    float texContrast = analysis.luminanceMap[idx] - 0.5f;
                    heightField[idx] = licVal + texContrast * 0.15f;
                }
            }

            // Convert height field to tangent-space normal map
            float bumpSlope = Mathf.Lerp(1.2f, 5.5f, Mathf.Clamp01(settings.strandBumpScale * 0.65f));
            for (int y = 0; y < h; y++)
            {
                int ym = Mathf.Max(0, y - 1);
                int yp = Mathf.Min(h - 1, y + 1);
                int row = y * w;

                for (int x = 0; x < w; x++)
                {
                    int xm = Mathf.Max(0, x - 1);
                    int xp = Mathf.Min(w - 1, x + 1);

                    float hL = heightField[row + xm];
                    float hR = heightField[row + xp];
                    float hD = heightField[ym * w + x];
                    float hU = heightField[yp * w + x];

                    float dx = (hR - hL) * bumpSlope;
                    float dy = (hU - hD) * bumpSlope;

                    float occ = analysis.paddedOccupancyMask[row + x];
                    dx *= occ;
                    dy *= occ;

                    Vector3 normal = new Vector3(-dx, -dy, 1f).normalized;
                    pixels[row + x] = new Color(
                        normal.x * 0.5f + 0.5f,
                        normal.y * 0.5f + 0.5f,
                        normal.z * 0.5f + 0.5f,
                        1f);
                }
            }

            return CreateTexture(w, h, pixels, true, "HairFlowNormal_Preview", TextureWrapMode.Repeat);
        }

        public static Texture2D GenerateGlossMatCap(
            HairGlossSettings settings,
            Color averageMainTexColor,
            int overrideSize = 0)
        {
            int size = overrideSize > 0
                ? Mathf.Clamp(overrideSize, 128, 1024)
                : Mathf.Clamp(settings.textureResolution, 256, 1024);

            Color tint = Color.Lerp(
                settings.glossColor,
                new Color(
                    settings.glossColor.r * averageMainTexColor.r * 1.35f,
                    settings.glossColor.g * averageMainTexColor.g * 1.35f,
                    settings.glossColor.b * averageMainTexColor.b * 1.35f,
                    1f),
                settings.mainTexColorBlend);

            float width = Mathf.Max(0.03f, settings.highlightWidth * 0.55f);
            float coreExp = Mathf.Lerp(1.6f, 4.8f, settings.angelRingSharpness);
            float strandShimmerStrength = Mathf.Clamp01(settings.strandBumpScale) * 0.24f;

            Color[] pixels = new Color[size * size];

            for (int py = 0; py < size; py++)
            {
                float y = ((py + 0.5f) / size) * 2f - 1f;
                int row = py * size;

                for (int px = 0; px < size; px++)
                {
                    float x = ((px + 0.5f) / size) * 2f - 1f;
                    float r2 = x * x + y * y;

                    // Clamp outside sphere rim softly for clean mipmapping
                    float sx = x;
                    float sy = y;
                    float nz;
                    float rimFade = 1f;
                    if (r2 > 1f)
                    {
                        float r = Mathf.Sqrt(r2);
                        sx /= r;
                        sy /= r;
                        nz = 0f;
                        rimFade = Mathf.Clamp01(1f - (r - 1f) * 8f);
                    }
                    else
                    {
                        nz = Mathf.Sqrt(1f - r2);
                    }

                    float band;
                    if (settings.enableAngelRing)
                    {
                        float yCenter = settings.angelRingHeight - settings.angelRingArch * (sx * sx);
                        float dy = (sy - yCenter) / width;

                        float sharpCore = Mathf.Exp(-dy * dy * coreExp);
                        float softHalo = Mathf.Exp(-dy * dy * 0.85f);
                        float secondaryThin = Mathf.Exp(-Mathf.Pow(dy - 0.9f, 2f) * 6.5f) * 0.22f;

                        float glossProfile = 0.72f * sharpCore + 0.28f * softHalo + secondaryThin;
                        float matteProfile = Mathf.Pow(softHalo, 0.75f) * 0.62f;

                        band = Mathf.Lerp(matteProfile, glossProfile, settings.smoothness);
                    }
                    else
                    {
                        float hemi = Mathf.Clamp01(0.5f + 0.5f * sy);
                        band = Mathf.Pow(hemi, Mathf.Lerp(1.2f, 3.0f, settings.smoothness)) * 0.55f;
                    }

                    // Multi-frequency fiber shimmer across X
                    float shimmer = 1f;
                    if (strandShimmerStrength > 0.001f)
                    {
                        float wave = 0.5f + 0.3f * Mathf.Sin(sx * 38f) + 0.2f * Mathf.Sin(sx * 91f + 1.3f);
                        shimmer = Mathf.Lerp(1f - strandShimmerStrength, 1f + strandShimmerStrength * 0.35f, wave);
                    }

                    float edgeAtten = Mathf.Pow(Mathf.Max(0.04f, nz), 0.35f) * Mathf.Clamp01(1f - sx * sx * 0.5f);
                    float value = band * shimmer * edgeAtten * rimFade;

                    pixels[row + px] = new Color(
                        Mathf.Clamp01(tint.r * value),
                        Mathf.Clamp01(tint.g * value),
                        Mathf.Clamp01(tint.b * value),
                        1f);
                }
            }

            return CreateTexture(size, size, pixels, false, "HairGlossMatCap_Preview", TextureWrapMode.Clamp);
        }

        public static Texture2D GenerateGlossMask(
            HairFlowAnalyzer.AnalysisResult analysis,
            HairGlossSettings settings)
        {
            int w = analysis.width;
            int h = analysis.height;
            int count = w * h;

            Color[] pixels = new Color[count];
            for (int i = 0; i < count; i++)
            {
                float rt = analysis.rootTipMap[i];
                float rootMask = settings.rootFalloff > 0.001f
                    ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(rt / settings.rootFalloff))
                    : 1f;
                float tipMask = settings.tipFalloff > 0.001f
                    ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - rt) / settings.tipFalloff))
                    : 1f;

                float occ = analysis.paddedOccupancyMask[i];
                float creaseMod = Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(analysis.luminanceMap[i] * 1.5f));
                float val = Mathf.Clamp01(occ * rootMask * tipMask * creaseMod);

                pixels[i] = new Color(val, val, val, 1f);
            }

            return CreateTexture(w, h, pixels, true, "HairGlossMask_Preview", TextureWrapMode.Clamp);
        }

        public static SavedTextureSet SaveTexturesAsAssets(
            string outputFolder,
            string baseName,
            Texture2D normalMap,
            Texture2D matCap,
            Texture2D mask,
            Texture2D tangentMap,
            HairGlossSettings settings,
            string[] ownedTexturePaths = null)
        {
            if (!TryNormalizeOutputFolder(outputFolder, out string normalizedFolder, out string error))
                throw new ArgumentException(error, nameof(outputFolder));
            outputFolder = normalizedFolder;
            if (ownedTexturePaths != null && ownedTexturePaths.Length != 4)
                throw new ArgumentException("Four owned texture paths are required.", nameof(ownedTexturePaths));
            if (ownedTexturePaths != null)
            {
                foreach (string path in ownedTexturePaths)
                {
                    if (string.IsNullOrEmpty(path) || !TryNormalizeOutputFolder(Path.GetDirectoryName(path), out _, out error))
                        throw new ArgumentException("Owned textures must be under Assets/.", nameof(ownedTexturePaths));
                }
            }
            EnsureFolderExists(outputFolder);
            string safeBase = SanitizeFileName(baseName);

            string normalPath = ownedTexturePaths != null ? ownedTexturePaths[0]
                : AssetDatabase.GenerateUniqueAssetPath(Path.Combine(outputFolder, $"{safeBase}_FlowNormal.png").Replace('\\', '/'));
            string matCapPath = ownedTexturePaths != null ? ownedTexturePaths[1]
                : AssetDatabase.GenerateUniqueAssetPath(Path.Combine(outputFolder, $"{safeBase}_GlossMatCap.png").Replace('\\', '/'));
            string maskPath = ownedTexturePaths != null ? ownedTexturePaths[2]
                : AssetDatabase.GenerateUniqueAssetPath(Path.Combine(outputFolder, $"{safeBase}_GlossMask.png").Replace('\\', '/'));
            string tangentPath = ownedTexturePaths != null ? ownedTexturePaths[3]
                : AssetDatabase.GenerateUniqueAssetPath(Path.Combine(outputFolder, $"{safeBase}_AnisotropyTangent.png").Replace('\\', '/'));

            File.WriteAllBytes(normalPath, normalMap.EncodeToPNG());
            File.WriteAllBytes(matCapPath, matCap.EncodeToPNG());
            File.WriteAllBytes(maskPath, mask.EncodeToPNG());
            File.WriteAllBytes(tangentPath, tangentMap.EncodeToPNG());

            AssetDatabase.ImportAsset(normalPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(matCapPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(maskPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(tangentPath, ImportAssetOptions.ForceUpdate);
            ConfigureTextureImporter(tangentPath, TextureImporterType.NormalMap, false, TextureWrapMode.Repeat, settings.textureResolution);

            ConfigureTextureImporter(
                normalPath,
                TextureImporterType.NormalMap,
                false,
                TextureWrapMode.Repeat,
                settings.textureResolution);

            ConfigureTextureImporter(
                matCapPath,
                TextureImporterType.Default,
                true,
                TextureWrapMode.Clamp,
                Mathf.Clamp(settings.textureResolution, 256, 1024));

            ConfigureTextureImporter(
                maskPath,
                TextureImporterType.Default,
                false,
                TextureWrapMode.Clamp,
                settings.textureResolution);

            return new SavedTextureSet
            {
                normalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath),
                matCap = AssetDatabase.LoadAssetAtPath<Texture2D>(matCapPath),
                mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath),
                tangentMap = AssetDatabase.LoadAssetAtPath<Texture2D>(tangentPath),
                normalMapPath = normalPath,
                matCapPath = matCapPath,
                maskPath = maskPath,
                tangentMapPath = tangentPath
            };
        }

        public static Texture2D ImportPreviewNormal(string path, Texture2D texture)
        {
            if (string.IsNullOrEmpty(path) || !TryNormalizeOutputFolder(Path.GetDirectoryName(path), out _, out string error))
                throw new ArgumentException("Preview normals must be under Assets/.", nameof(path));
            File.WriteAllBytes(path, texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureTextureImporter(path, TextureImporterType.NormalMap, false, TextureWrapMode.Repeat, texture.width);
            Texture2D imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (imported == null) throw new InvalidOperationException($"Could not import preview normal: {path}");
            return imported;
        }

        public static void EnsureFolderExists(string assetFolderPath)
        {
            if (!TryNormalizeOutputFolder(assetFolderPath, out string normalized, out string error))
                throw new ArgumentException(error, nameof(assetFolderPath));
            if (AssetDatabase.IsValidFolder(normalized))
            {
                return;
            }

            string[] parts = normalized.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(next))
                        throw new IOException($"Could not create asset folder: {next}");
                }
                current = next;
            }
        }

        public static bool TryNormalizeOutputFolder(string input, out string normalized, out string error)
        {
            normalized = null;
            error = "Output folder must be a relative folder strictly under Assets/, without '.' or '..'.";
            if (string.IsNullOrWhiteSpace(input) || Path.IsPathRooted(input) || input.StartsWith("\\", StringComparison.Ordinal)) return false;
            string[] parts = input.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "Assets") return false;
            foreach (string part in parts)
            {
                if (part == "." || part == ".." || part != part.Trim() || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    part.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0) return false;
                foreach (char c in part) if (char.IsControl(c)) return false;
            }
            try
            {
                string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar);
                string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assets), string.Join("/", parts)));
                if (!path.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
            normalized = string.Join("/", parts);
            error = null;
            return true;
        }

        public static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "Hair";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            string cleaned = raw;
            for (int i = 0; i < invalid.Length; i++)
            {
                cleaned = cleaned.Replace(invalid[i], '_');
            }
            return cleaned.Replace(' ', '_');
        }

        private static void ConfigureTextureImporter(
            string assetPath,
            TextureImporterType type,
            bool sRGB,
            TextureWrapMode wrapMode,
            int maxSize)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = type;
            if (type == TextureImporterType.NormalMap) importer.convertToNormalmap = false;
            importer.sRGBTexture = sRGB;
            importer.wrapMode = wrapMode;
            importer.maxTextureSize = maxSize;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int ix = Mathf.FloorToInt(x);
            int iy = Mathf.FloorToInt(y);
            float fx = x - ix;
            float fy = y - iy;

            float ux = fx * fx * (3f - 2f * fx);
            float uy = fy * fy * (3f - 2f * fy);

            float a = Hash01(ix, iy, seed);
            float b = Hash01(ix + 1, iy, seed);
            float c = Hash01(ix, iy + 1, seed);
            float d = Hash01(ix + 1, iy + 1, seed);

            return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
        }

        private static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0x00FFFFFF) / 16777215f;
            }
        }
    }
}
