using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HairGlossStudio.Editor.Tests
{
    public class HairGlossGenerationTests
    {
        [TestCase("Assets/HairGloss", "Assets/HairGloss")]
        [TestCase("Assets\\HairGloss\\Output\\", "Assets/HairGloss/Output")]
        [TestCase("Assets//HairGloss/", "Assets/HairGloss")]
        public void OutputFolderIsCanonicalized(string input, string expected)
        {
            Assert.IsTrue(HairTextureGenerator.TryNormalizeOutputFolder(input, out string actual, out string error), error);
            Assert.AreEqual(expected, actual);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets")]
        [TestCase("Assets/")]
        [TestCase("Assets/../Outside")]
        [TestCase("Assets/./Output")]
        [TestCase("/Assets/Output")]
        [TestCase("\\Assets\\Output")]
        [TestCase("Assets/.. /Output")]
        [TestCase("C:\\Assets\\Output")]
        [TestCase("Packages/Output")]
        [TestCase("Assets/Bad:Name")]
        public void InvalidOutputFolderIsRejectedBeforeCreation(string input)
        {
            Assert.IsFalse(HairTextureGenerator.TryNormalizeOutputFolder(input, out string path, out string error));
            Assert.IsNull(path);
            Assert.IsNotEmpty(error);
            Assert.Throws<ArgumentException>(() => HairTextureGenerator.EnsureFolderExists(input));
        }

        [Test]
        public void TangentMapEncodesFlowEvenWhenBumpIsZero()
        {
            var analysis = new HairFlowAnalyzer.AnalysisResult
            {
                width = 2,
                height = 2,
                flowField = new[] { Vector2.right, Vector2.up, Vector2.left, Vector2.zero }
            };
            Texture2D tangent = null;
            Texture2D bump = null;
            try
            {
                tangent = HairTextureGenerator.GenerateAnisotropyTangentMap(analysis);
                bump = HairTextureGenerator.GenerateFlowNormalMap(analysis, new HairGlossSettings { strandBumpScale = 0f });
                Color[] pixels = tangent.GetPixels();
                Vector2[] expected = { Vector2.right, Vector2.up, Vector2.left, Vector2.down };
                for (int i = 0; i < pixels.Length; i++)
                {
                    Vector2 decoded = new Vector2(pixels[i].r * 2f - 1f, pixels[i].g * 2f - 1f);
                    Assert.That(Vector2.Distance(decoded, expected[i]), Is.LessThan(0.01f));
                }
                Assert.That(bump.GetPixel(0, 0).r, Is.EqualTo(0.5f).Within(0.01f));
                Assert.That(pixels[0].r, Is.GreaterThan(0.99f));
            }
            finally
            {
                if (tangent != null) UnityEngine.Object.DestroyImmediate(tangent);
                if (bump != null) UnityEngine.Object.DestroyImmediate(bump);
            }
        }

        [Test]
        public void IncompleteFlowIsRejected()
        {
            Assert.Throws<ArgumentException>(() => HairTextureGenerator.GenerateAnisotropyTangentMap(null));
            Assert.Throws<ArgumentException>(() => HairTextureGenerator.GenerateAnisotropyTangentMap(new HairFlowAnalyzer.AnalysisResult()));
        }

        [Test]
        public void PreviewUsesDistinctImportedNormalsAndRestoresAroundSave()
        {
            GameObject root = new GameObject("PreviewTests");
            Material material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            Mesh firstMesh = new Mesh();
            Mesh secondMesh = new Mesh();
            HairGlossPreviewRenderer preview = new HairGlossPreviewRenderer();
            string previewFolder = null;
            try
            {
                var targets = new List<HairMaterialTarget>();
                foreach (Mesh mesh in new[] { firstMesh, secondMesh })
                {
                    var child = new GameObject("Hair");
                    child.transform.SetParent(root.transform);
                    Renderer renderer = child.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    targets.Add(new HairMaterialTarget
                    {
                        renderer = renderer, mesh = mesh, originalMaterial = material,
                        currentMaterial = material, isSelected = true, isLilToon = true
                    });
                }
                var settings = new HairGlossSettings { strandBumpScale = 0f };
                preview.UpdateScenePreview(targets, settings, null);
                IDictionary cache = (IDictionary)GetField(preview, "textureCache");
                Assert.AreEqual(2, cache.Count);
                var normals = new HashSet<Texture2D>();
                foreach (object textures in cache.Values)
                {
                    Texture2D normal = (Texture2D)GetField(textures, "normal");
                    normals.Add(normal);
                    foreach (string field in new[] { "normal", "tangent" })
                    {
                        Texture2D texture = (Texture2D)GetField(textures, field);
                        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
                        Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType);
                        Assert.IsFalse(importer.sRGBTexture);
                        Assert.IsFalse(importer.convertToNormalmap);
                    }
                }
                Assert.AreEqual(2, normals.Count);
                previewFolder = (string)GetField(preview, "previewFolder");
                preview.UpdateScenePreview(targets, settings, null);
                Assert.AreSame(cache, GetField(preview, "textureCache"));
                foreach (object textures in cache.Values) Assert.IsTrue(normals.Contains((Texture2D)GetField(textures, "normal")));

                Invoke(preview, "OnSceneSaving", root.scene, root.scene.path);
                Assert.IsFalse(preview.HasActiveScenePreview);
                foreach (HairMaterialTarget target in targets) Assert.AreEqual(material, target.renderer.sharedMaterial);
                Invoke(preview, "OnSceneSaved", root.scene);
                Invoke(preview, "ResumeScenePreview");
                Assert.IsTrue(preview.HasActiveScenePreview);
                Invoke(preview, "OnPlayModeStateChanged", PlayModeStateChange.ExitingEditMode);
                Assert.IsFalse(preview.HasActiveScenePreview);
                foreach (HairMaterialTarget target in targets) Assert.AreEqual(material, target.renderer.sharedMaterial);
            }
            finally
            {
                preview.Dispose();
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(firstMesh);
                UnityEngine.Object.DestroyImmediate(secondMesh);
            }
            Assert.IsFalse(AssetDatabase.IsValidFolder(previewFolder));
        }

        [Test]
        public void EmptyPreviewAndMissingTextureReadbackAreSafe()
        {
            using (var preview = new HairGlossPreviewRenderer())
            {
                preview.UpdateScenePreview(new List<HairMaterialTarget>(), new HairGlossSettings(), null);
                Assert.IsFalse(preview.HasActiveScenePreview);
                preview.UpdateScenePreview(null, null, null);
            }
            Assert.AreEqual(4, HairFlowAnalyzer.ReadTexturePixelsSafe(null, 2, 2).Length);
        }

        private static object GetField(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        }

        private static void Invoke(object target, string name, params object[] args)
        {
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        }
    }
}
