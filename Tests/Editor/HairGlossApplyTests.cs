using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HairGlossStudio.Editor.Tests
{
    public class HairGlossApplyTests
    {
        private string folder;
        private GameObject root;
        private Material original;
        private HairGlossStudioWindow window;
        private List<HairMaterialTarget> targets;
        private bool defaultStoreExisted;
        private bool defaultFolderExisted;
        private bool hadPreferences;
        private string preferences;
        private string SettingsKey => $"HairGlossStudio.Settings.{Application.dataPath}";

        [SetUp]
        public void SetUp()
        {
            defaultStoreExisted = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(HairGlossBackupStore.DefaultStoreAssetPath) != null;
            defaultFolderExisted = AssetDatabase.IsValidFolder("Assets/HairGlossStudio_Generated");
            folder = $"Assets/HairGlossApplyTests_{Guid.NewGuid():N}";
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            root = new GameObject($"HairGlossApplyTests_{Guid.NewGuid():N}");
            original = new Material(Shader.Find("Hidden/HairGlossStudio/Tests"));
            AssetDatabase.CreateAsset(original, $"{folder}/Original.mat");
            hadPreferences = EditorPrefs.HasKey(SettingsKey);
            preferences = EditorPrefs.GetString(SettingsKey, string.Empty);
            UnityEngine.Object selection = Selection.activeObject;
            try
            {
                Selection.activeObject = null;
                window = ScriptableObject.CreateInstance<HairGlossStudioWindow>();
            }
            finally { Selection.activeObject = selection; }
            SetField("targetRoot", root);
            SetField("settings", new HairGlossSettings { outputFolder = folder, textureResolution = 64, strandBumpScale = 0f });
            targets = (List<HairMaterialTarget>)GetField("targets");
            for (int i = 0; i < 2; i++)
            {
                var child = new GameObject("Hair");
                child.transform.SetParent(root.transform);
                Renderer renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = original;
                targets.Add(new HairMaterialTarget { renderer = renderer, originalMaterial = original, currentMaterial = original, isSelected = true, isLilToon = true });
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (window != null) UnityEngine.Object.DestroyImmediate(window);
            if (hadPreferences) EditorPrefs.SetString(SettingsKey, preferences);
            else EditorPrefs.DeleteKey(SettingsKey);
            HairGlossBackupStore store = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(HairGlossBackupStore.DefaultStoreAssetPath);
            if (targets != null)
            {
                foreach (HairMaterialTarget target in targets)
                {
                    if (store != null) store.RestoreTarget(target.renderer, 0);
                    Undo.ClearUndo(target.renderer);
                }
            }
            if (store != null && !defaultStoreExisted)
            {
                Undo.ClearUndo(store);
                AssetDatabase.DeleteAsset(HairGlossBackupStore.DefaultStoreAssetPath);
            }
            if (!defaultFolderExisted) AssetDatabase.DeleteAsset("Assets/HairGlossStudio_Generated");
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        [TestCase(0f, 0)]
        [TestCase(0.1f, 0)]
        [TestCase(0.175f, 0)]
        [TestCase(0.25f, 0)]
        [TestCase(0.325f, 0)]
        [TestCase(0.4f, 0)]
        [TestCase(0.5f, 1)]
        [TestCase(0.75f, 1)]
        [TestCase(0.9f, 1)]
        [TestCase(2f, 2)]
        public void CancellationBetweenStagesLeavesOnlyCompletedTargetsRecorded(float cancelAt, int expectedApplied)
        {
            Apply((message, progress) => progress >= cancelAt);
            HairGlossBackupStore store = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(HairGlossBackupStore.DefaultStoreAssetPath);
            Assert.IsNotNull(store);
            for (int i = 0; i < targets.Count; i++)
            {
                bool applied = i < expectedApplied;
                Assert.AreEqual(applied, targets[i].renderer.sharedMaterial != original);
                Assert.AreEqual(applied, store.TryGetRecord(targets[i].renderer, 0, out RendererMaterialRecord record));
                if (applied)
                {
                    Assert.AreEqual(original, record.originalMaterial);
                    Assert.AreEqual(targets[i].renderer.sharedMaterial, record.generatedMaterial);
                    Assert.AreEqual(4, record.generatedTexturePaths.Length);
                    Assert.IsTrue(store.OwnsGeneratedAssets(targets[i].renderer, 0));
                }
            }
            Assert.AreEqual(expectedApplied * 4, AssetDatabase.FindAssets("t:Texture2D", new[] { folder }).Length);
            if (cancelAt <= 1f) Assert.That((string)GetField("statusMessage"), Does.Contain("キャンセル"));
        }

        [Test]
        public void FailureBeforeSavingDoesNotAssignOrRecordPartialTarget()
        {
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => Apply((message, progress) =>
            {
                if (progress >= 0.25f) throw new InvalidOperationException("Injected stage failure");
                return false;
            }));
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
            HairGlossBackupStore store = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(HairGlossBackupStore.DefaultStoreAssetPath);
            foreach (HairMaterialTarget target in targets)
            {
                Assert.AreEqual(original, target.renderer.sharedMaterial);
                Assert.IsFalse(store.TryGetRecord(target.renderer, 0, out _));
            }
            Assert.IsEmpty(AssetDatabase.FindAssets("t:Texture2D", new[] { folder }));
        }

        [Test]
        public void EmptySelectionDoesNotStartGeneration()
        {
            foreach (HairMaterialTarget target in targets) target.isSelected = false;
            Apply((message, progress) => { Assert.Fail("Empty selection must not start generation"); return false; });
            Assert.IsEmpty(AssetDatabase.FindAssets("t:Texture2D", new[] { folder }));
            foreach (HairMaterialTarget target in targets) Assert.AreEqual(original, target.renderer.sharedMaterial);
        }

        [Test]
        public void ReapplyInNewFolderKeepsOldOutputsAndEarliestOriginal()
        {
            Apply((message, progress) => false);
            HairGlossBackupStore store = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(HairGlossBackupStore.DefaultStoreAssetPath);
            Assert.IsTrue(store.TryGetRecord(targets[0].renderer, 0, out RendererMaterialRecord record));
            Material oldMaterial = record.generatedMaterial;
            string[] oldTextures = (string[])record.generatedTexturePaths.Clone();
            var settings = (HairGlossSettings)GetField("settings");
            settings.outputFolder = $"{folder}/Moved";
            settings.glossColor = Color.red;
            Apply((message, progress) => false);
            string prefix = $"{settings.outputFolder}/{root.name}/";
            Assert.IsTrue(store.TryGetRecord(targets[0].renderer, 0, out record));
            Assert.AreEqual(original, record.originalMaterial);
            Assert.AreNotEqual(oldMaterial, record.generatedMaterial);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(oldMaterial)));
            Assert.That(AssetDatabase.GetAssetPath(record.generatedMaterial), Does.StartWith(prefix));
            foreach (string path in record.generatedTexturePaths) Assert.That(path, Does.StartWith(prefix));
            foreach (string path in oldTextures) Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            Assert.IsTrue(store.RestoreTarget(targets[0].renderer, 0));
            Assert.AreEqual(original, targets[0].renderer.sharedMaterial);
        }

        private void Apply(Func<string, float, bool> cancel)
        {
            typeof(HairGlossStudioWindow).GetMethod("ApplyGlossToSelectedTargets", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, new object[] { cancel });
        }

        private object GetField(string name) => typeof(HairGlossStudioWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        private void SetField(string name, object value) => typeof(HairGlossStudioWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value);
    }
}
