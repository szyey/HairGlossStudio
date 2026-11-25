using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace HairGlossStudio.Editor.Tests
{
    public class HairGlossBatchThreeTests
    {
        [Test]
        public void AnalysisCacheReusesIdenticalAndEmptyInputsAndClears()
        {
            var cache = new HairFlowAnalyzer.AnalysisCache();
            var first = cache.GetOrAnalyze(null, 0, null, 64, 0f);
            Assert.AreSame(first, cache.GetOrAnalyze(null, 0, null, 64, 0f));
            Assert.AreSame(first, cache.GetOrAnalyze(null, 0, null, 1, 0f));
            Assert.AreNotSame(first, cache.GetOrAnalyze(null, 1, null, 64, 0f));
            Assert.AreNotSame(first, cache.GetOrAnalyze(null, 0, null, 128, 0f));
            Assert.AreNotSame(first, cache.GetOrAnalyze(null, 0, null, 64, 0.5f));
            Assert.AreEqual(Vector2.down, first.flowField[0]);
            cache.Clear();
            Assert.AreNotSame(first, cache.GetOrAnalyze(null, 0, null, 64, 0f));
        }

        [Test]
        public void AnalysisKeyIncludesMeshSubmeshTextureResolutionAndWeight()
        {
            Mesh mesh = new Mesh();
            Texture2D texture = new Texture2D(2, 2);
            try
            {
                string key = HairFlowAnalyzer.GetAnalysisCacheKey(null, 0, null, 64, 0f);
                Assert.AreNotEqual(key, HairFlowAnalyzer.GetAnalysisCacheKey(mesh, 0, null, 64, 0f));
                Assert.AreNotEqual(key, HairFlowAnalyzer.GetAnalysisCacheKey(null, 1, null, 64, 0f));
                Assert.AreNotEqual(key, HairFlowAnalyzer.GetAnalysisCacheKey(null, 0, texture, 64, 0f));
                Assert.AreNotEqual(key, HairFlowAnalyzer.GetAnalysisCacheKey(null, 0, null, 128, 0f));
                Assert.AreNotEqual(key, HairFlowAnalyzer.GetAnalysisCacheKey(null, 0, null, 64, 0.5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void CountedAnalysisIsEvictedAfterItsLastUse()
        {
            var cache = new HairFlowAnalyzer.AnalysisCache();
            string firstKey = HairFlowAnalyzer.GetAnalysisCacheKey(null, 0, null, 64, 0f);
            string secondKey = HairFlowAnalyzer.GetAnalysisCacheKey(null, 1, null, 64, 0f);
            cache.Retain(firstKey);
            cache.Retain(secondKey);
            cache.Retain(firstKey);
            var first = cache.GetOrAnalyze(null, 0, null, 64, 0f);
            var second = cache.GetOrAnalyze(null, 1, null, 64, 0f);
            cache.Release(firstKey);
            Assert.AreSame(first, cache.GetOrAnalyze(null, 0, null, 64, 0f));
            cache.Release(secondKey);
            Assert.AreNotSame(second, cache.GetOrAnalyze(null, 1, null, 64, 0f));
            cache.Release(firstKey);
            Assert.AreNotSame(first, cache.GetOrAnalyze(null, 0, null, 64, 0f));
            cache.Retain(firstKey);
            cache.Clear();
            cache.Retain(firstKey);
            first = cache.GetOrAnalyze(null, 0, null, 64, 0f);
            cache.Release(firstKey);
            Assert.AreNotSame(first, cache.GetOrAnalyze(null, 0, null, 64, 0f));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void DilationPreservesOriginalArraysAcrossBufferSwaps(int iterations)
        {
            const int size = 5;
            var flow = new Vector2[size * size];
            var occupancy = new float[size * size];
            var rootTip = new float[size * size];
            flow[12] = Vector2.down;
            occupancy[12] = 1f;
            rootTip[12] = 0.3f;
            typeof(HairFlowAnalyzer).GetMethod("DilateMeshData", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { size, flow, occupancy, rootTip, iterations });
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool filled = Mathf.Max(Mathf.Abs(x - 2), Mathf.Abs(y - 2)) <= iterations;
                    int index = y * size + x;
                    Assert.AreEqual(filled ? 1f : 0f, occupancy[index]);
                    Assert.AreEqual(filled ? Vector2.down : Vector2.zero, flow[index]);
                    Assert.That(rootTip[index], Is.EqualTo(filled ? 0.3f : 0f).Within(0.00001f));
                }
            }
        }

        [TestCase(0f, 0f, MatCapSlotMode.MatCap1st)]
        [TestCase(0f, 1f, MatCapSlotMode.MatCap1st)]
        [TestCase(1f, 0f, MatCapSlotMode.MatCap2nd)]
        [TestCase(1f, 1f, MatCapSlotMode.Auto)]
        public void AutoMatCapChoosesFirstFreeSlotOrSkips(float first, float second, MatCapSlotMode expected)
        {
            Material material = CreateMaterial();
            Texture2D generated = new Texture2D(2, 2);
            try
            {
                var settings = new HairGlossSettings();
                Assert.AreEqual(MatCapSlotMode.Auto, settings.matCapSlot);
                material.SetFloat("_UseMatCap", first);
                material.SetFloat("_UseMatCap2nd", second);
                material.SetTexture("_MatCapTex", Texture2D.blackTexture);
                material.SetTexture("_MatCap2ndTex", Texture2D.grayTexture);
                material.SetFloat("_AnisotropyMatCapStrength", 0.2f);
                material.SetFloat("_Anisotropy2ndMatCapStrength", 0.3f);

                bool hasSlot = expected != MatCapSlotMode.Auto;
                Assert.AreEqual(hasSlot, LilToonMaterialConfigurator.TryResolveMatCapSlot(material, settings.matCapSlot, out MatCapSlotMode actual));
                Assert.AreEqual(expected, actual);
                string warning = null;
                Application.LogCallback callback = (message, trace, type) => { if (type == LogType.Warning) warning = message; };
                Application.logMessageReceived += callback;
                try
                {
                    Assert.AreEqual(hasSlot, LilToonMaterialConfigurator.ConfigureMaterial(material, settings, null, generated, null, generated));
                }
                finally { Application.logMessageReceived -= callback; }
                Assert.AreEqual(expected == MatCapSlotMode.MatCap1st ? generated : Texture2D.blackTexture, material.GetTexture("_MatCapTex"));
                Assert.AreEqual(expected == MatCapSlotMode.MatCap2nd ? generated : Texture2D.grayTexture, material.GetTexture("_MatCap2ndTex"));
                Assert.AreEqual(expected == MatCapSlotMode.MatCap1st ? 1f : first, material.GetFloat("_UseMatCap"));
                Assert.AreEqual(expected == MatCapSlotMode.MatCap2nd ? 1f : second, material.GetFloat("_UseMatCap2nd"));
                Assert.AreEqual(expected == MatCapSlotMode.MatCap1st ? settings.anisotropyMatCapStrength : 0.2f, material.GetFloat("_AnisotropyMatCapStrength"));
                Assert.AreEqual(expected == MatCapSlotMode.MatCap2nd ? settings.anisotropyMatCapStrength : 0.3f, material.GetFloat("_Anisotropy2ndMatCapStrength"));
                Assert.AreEqual(generated, material.GetTexture("_AnisotropyTangentMap"));
                if (!hasSlot) Assert.That(warning, Does.Contain("MatCap was skipped"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(generated);
            }
        }

        [TestCase(MatCapSlotMode.MatCap1st)]
        [TestCase(MatCapSlotMode.MatCap2nd)]
        public void ExplicitMatCapSelectionCanReplaceOccupiedSlot(MatCapSlotMode requested)
        {
            Material material = CreateMaterial();
            try
            {
                material.SetFloat("_UseMatCap", 1f);
                material.SetFloat("_UseMatCap2nd", 1f);
                material.SetTexture("_MatCapTex", Texture2D.blackTexture);
                material.SetTexture("_MatCap2ndTex", Texture2D.blackTexture);
                Assert.IsTrue(LilToonMaterialConfigurator.ConfigureMaterial(material,
                    new HairGlossSettings { matCapSlot = requested }, null, Texture2D.whiteTexture, null, null));
                Assert.AreEqual(requested == MatCapSlotMode.MatCap1st ? Texture2D.whiteTexture : Texture2D.blackTexture, material.GetTexture("_MatCapTex"));
                Assert.AreEqual(requested == MatCapSlotMode.MatCap2nd ? Texture2D.whiteTexture : Texture2D.blackTexture, material.GetTexture("_MatCap2ndTex"));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void MissingMaterialOrUnsupportedShaderHasNoMatCapSlot()
        {
            Assert.IsFalse(LilToonMaterialConfigurator.TryResolveMatCapSlot(null, MatCapSlotMode.Auto, out _));
            Assert.IsFalse(LilToonMaterialConfigurator.ConfigureMaterial(null, new HairGlossSettings(), null, null, null, null));
            Material material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            try
            {
                Assert.IsFalse(LilToonMaterialConfigurator.TryResolveMatCapSlot(material, MatCapSlotMode.Auto, out _));
                Assert.IsFalse(LilToonMaterialConfigurator.ConfigureMaterial(material, null, null, null, null, null));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void SyntheticDomeHasFullyLitCenterAndFadesToBackgroundAtEdge()
        {
            Texture2D dome = (Texture2D)typeof(HairGlossPreviewRenderer)
                .GetMethod("RenderSyntheticHairDome", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { new HairGlossSettings { strandBumpScale = 0f, enableAnisotropy = false, glossIntensity = 0f }, new Color(0.5f, 0.5f, 0.5f), null, null });
            try
            {
                int center = dome.width / 2;
                Assert.That(dome.GetPixel(center, center).r, Is.GreaterThan(0.4f));
                int edge = Mathf.FloorToInt(dome.width * 0.96f - 0.5f);
                float background = Mathf.Lerp(0.09f, 0.14f, (center + 0.5f) / dome.height);
                Assert.That(dome.GetPixel(edge, center).r, Is.EqualTo(background).Within(0.006f));
                Assert.That(dome.GetPixel(edge + 1, center).r, Is.EqualTo(background).Within(0.003f));
            }
            finally { UnityEngine.Object.DestroyImmediate(dome); }
        }

        private static Material CreateMaterial()
        {
            Shader shader = Shader.Find("Hidden/HairGlossStudio/Tests");
            Assert.IsNotNull(shader);
            return new Material(shader);
        }
    }
}
