using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HairGlossStudio.Editor.Tests
{
    public class HairGlossBackupStoreTests
    {
        private string folder;
        private HairGlossBackupStore store;
        private GameObject root;
        private Material original;
        private Material generated;

        [SetUp]
        public void SetUp()
        {
            folder = $"Assets/HairGlossStudioTests_{Guid.NewGuid():N}";
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            store = HairGlossBackupStore.LoadOrCreate($"{folder}/Backup.asset");
            root = new GameObject("HairGlossStudioTests");
            Shader shader = Shader.Find("Hidden/InternalErrorShader");
            original = new Material(shader);
            generated = new Material(shader);
            AssetDatabase.CreateAsset(original, $"{folder}/Original.mat");
            AssetDatabase.CreateAsset(generated, $"{folder}/Generated.mat");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(root);
            Undo.ClearUndo(store);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.DeleteAsset(folder);
        }

        private List<RendererMaterialRecord> Records =>
            (List<RendererMaterialRecord>)typeof(HairGlossBackupStore)
                .GetField("records", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);

        private bool Find(string globalId, string path)
        {
            object[] args = { globalId, path, 0, -1, null };
            return (bool)typeof(HairGlossBackupStore)
                .GetMethod("TryFindRecordIndex", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(store, args);
        }

        [Test]
        public void EmptyStoreDoesNotMatch()
        {
            Assert.IsFalse(Find(string.Empty, "scene|0:Hair"));
        }

        [Test]
        public void ResolvableGlobalIdPreventsHierarchyFallback()
        {
            Records.Add(new RendererMaterialRecord
            {
                rendererGlobalObjectId = HairGlossBackupStore.GetGlobalIdString(original),
                rendererHierarchyPath = "scene|0:Hair"
            });
            Assert.IsFalse(Find(HairGlossBackupStore.GetGlobalIdString(generated), "scene|0:Hair"));
            Assert.IsTrue(Find(HairGlossBackupStore.GetGlobalIdString(original), "renamed"));
        }

        [TestCase("")]
        [TestCase("invalid legacy id")]
        public void LegacyFallbackRequiresUniqueScopedPath(string globalId)
        {
            Records.Add(new RendererMaterialRecord
            {
                rendererGlobalObjectId = globalId,
                rendererHierarchyPath = "scene|0:Hair"
            });
            Assert.IsTrue(Find(string.Empty, "scene|0:Hair"));
            Assert.IsFalse(Find(string.Empty, "otherScene|0:Hair"));
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = "scene|0:Hair" });
            Assert.IsFalse(Find(string.Empty, "scene|0:Hair"));
        }

        [Test]
        public void DuplicateGlobalIdsAreRejected()
        {
            string id = HairGlossBackupStore.GetGlobalIdString(original);
            Records.Add(new RendererMaterialRecord { rendererGlobalObjectId = id });
            Records.Add(new RendererMaterialRecord { rendererGlobalObjectId = id });
            Assert.IsFalse(Find(id, string.Empty));
        }

        [TestCase("")]
        [TestCase("unresolvable legacy id")]
        public void NameOnlyLegacyRecordsAreUpgradedOnMatch(string globalId)
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = generated;
            Records.Add(new RendererMaterialRecord
            {
                rendererHierarchyPath = root.name, rendererGlobalObjectId = globalId,
                originalMaterial = original, generatedMaterial = generated
            });
            Assert.AreEqual(store, HairGlossBackupStore.FindForTarget(renderer, 0));
            Assert.IsTrue(store.TryGetRecord(renderer, 0, out RendererMaterialRecord record));
            Assert.AreEqual(HairGlossBackupStore.GetHierarchyPath(root.transform), record.rendererHierarchyPath);
            Assert.AreEqual(HairGlossBackupStore.GetGlobalIdString(renderer), record.rendererGlobalObjectId);
            Assert.AreEqual(original, record.originalMaterial);
            Assert.IsTrue(store.RestoreTarget(renderer, 0));
            Assert.AreEqual(original, renderer.sharedMaterial);
        }

        [Test]
        public void DuplicateNameOnlyLegacyRecordsAreNotMigrated()
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = root.name, originalMaterial = original });
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = root.name, originalMaterial = original });
            Assert.IsNull(HairGlossBackupStore.FindForTarget(renderer, 0, out bool ambiguous));
            Assert.IsTrue(ambiguous);
            Assert.IsFalse(store.TryGetRecord(renderer, 0, out _));
            foreach (RendererMaterialRecord record in Records) Assert.AreEqual(root.name, record.rendererHierarchyPath);
        }

        [Test]
        public void ResolvableLegacyIdPreventsNameOnlyFallback()
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            Records.Add(new RendererMaterialRecord
            {
                rendererHierarchyPath = root.name,
                rendererGlobalObjectId = HairGlossBackupStore.GetGlobalIdString(original),
                originalMaterial = original
            });
            Assert.IsFalse(store.TryGetRecord(renderer, 0, out _));
            Assert.AreEqual(root.name, Records[0].rendererHierarchyPath);
        }

        [Test]
        public void UnsavedSceneFallbackMatchesAfterSavingButRejectsAmbiguousRecords()
        {
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = "|0:Hair" });
            Assert.IsTrue(Find(string.Empty, "Assets/Saved.unity|0:Hair"));
            Assert.IsFalse(Find(string.Empty, "Assets/Saved.unity|1:Hair"));
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = "Assets/Saved.unity|0:Hair" });
            Assert.IsFalse(Find(string.Empty, "Assets/Saved.unity|0:Hair"));
        }

        [Test]
        public void FirstSceneSaveRefreshesBackupIdentityAndPreservesOriginalOnReapply()
        {
            Scene originalScene = root.scene;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                Renderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = generated;
                store.RegisterBackup(renderer, 0, original, generated, Array.Empty<string>());
                Assert.That(store.Records[0].rendererHierarchyPath, Does.StartWith("|"));
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, $"{folder}/Saved.unity"));
                Assert.IsTrue(store.TryGetRecord(renderer, 0, out RendererMaterialRecord record));
                Assert.AreEqual(HairGlossBackupStore.GetHierarchyPath(root.transform), record.rendererHierarchyPath);
                Assert.AreEqual(HairGlossBackupStore.GetGlobalIdString(renderer), record.rendererGlobalObjectId);
                store.RegisterBackup(renderer, 0, generated, generated, Array.Empty<string>());
                Assert.AreEqual(1, store.Records.Count);
                Assert.AreEqual(original, record.originalMaterial);
                Assert.IsTrue(store.RestoreTarget(renderer, 0));
                Assert.AreEqual(original, renderer.sharedMaterial);
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, originalScene);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void AmbiguousDiscoveryDoesNotCreateAnotherBackup()
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            string path = HairGlossBackupStore.GetHierarchyPath(renderer.transform);
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = path });
            Records.Add(new RendererMaterialRecord { rendererHierarchyPath = path });
            Assert.IsNull(HairGlossBackupStore.FindForTarget(renderer, 0, out bool ambiguous));
            Assert.IsTrue(ambiguous);
            Assert.Throws<InvalidOperationException>(() =>
                store.RegisterBackup(renderer, 0, original, generated, Array.Empty<string>()));
            Assert.AreEqual(2, store.Records.Count);
        }

        [Test]
        public void HierarchyPathIncludesSceneAndSiblingIndices()
        {
            var first = new GameObject("Hair");
            var second = new GameObject("Hair");
            first.transform.SetParent(root.transform);
            second.transform.SetParent(root.transform);
            string path = HairGlossBackupStore.GetHierarchyPath(first.transform);
            Assert.That(path, Does.StartWith(root.scene.path + "|"));
            Assert.That(path, Does.EndWith("/0:Hair"));
            Assert.AreNotEqual(path, HairGlossBackupStore.GetHierarchyPath(second.transform));
        }

        [Test]
        public void RevertUndoRestoresMaterialAndBackupRecord()
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { generated };
            store.RegisterBackup(renderer, 0, original, generated, Array.Empty<string>());
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Assert.IsTrue(store.RestoreTarget(renderer, 0));
            Assert.AreEqual(original, renderer.sharedMaterials[0]);
            Assert.AreEqual(0, store.Records.Count);
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            Undo.PerformUndo();
            Assert.AreEqual(generated, renderer.sharedMaterials[0]);
            Assert.IsTrue(store.TryGetRecord(renderer, 0, out _));
        }

        [Test]
        public void StoreDiscoveryDoesNotDependOnOutputFolder()
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            store.RegisterBackup(renderer, 0, original, generated, Array.Empty<string>());
            Assert.AreEqual(store, HairGlossBackupStore.FindForTarget(renderer, 0));
            Assert.IsFalse(store.OwnsGeneratedAssets(renderer, 0));
        }

        [Test]
        public void NewTargetsWithSameNameGetSeparateTextureAssets()
        {
            Texture2D texture = new Texture2D(32, 32);
            try
            {
                var settings = new HairGlossSettings { textureResolution = 32 };
                HairTextureGenerator.SavedTextureSet first = HairTextureGenerator.SaveTexturesAsAssets(
                    folder, "Hair", texture, texture, texture, texture, settings);
                string originalGuid = AssetDatabase.AssetPathToGUID(first.normalMapPath);
                HairTextureGenerator.SavedTextureSet second = HairTextureGenerator.SaveTexturesAsAssets(
                    folder, "Hair", texture, texture, texture, texture, settings);
                Assert.AreNotEqual(first.normalMapPath, second.normalMapPath);
                Assert.AreNotEqual(first.matCapPath, second.matCapPath);
                Assert.AreNotEqual(first.maskPath, second.maskPath);
                Assert.AreNotEqual(first.tangentMapPath, second.tangentMapPath);
                Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(first.normalMapPath));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void OwnershipRejectsReplacedTextureGuid()
        {
            Renderer renderer = root.AddComponent<MeshRenderer>();
            string[] paths = new string[4];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = $"{folder}/Texture{i}.asset";
                AssetDatabase.CreateAsset(new Texture2D(2, 2), paths[i]);
            }
            store.RegisterBackup(renderer, 0, original, generated, paths);
            Assert.IsTrue(store.OwnsGeneratedAssets(renderer, 0));
            AssetDatabase.DeleteAsset(paths[0]);
            AssetDatabase.CreateAsset(new Texture2D(2, 2), paths[0]);
            Assert.IsFalse(store.OwnsGeneratedAssets(renderer, 0));
        }
    }
}
