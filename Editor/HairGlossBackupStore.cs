using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    [Serializable]
    public class RendererMaterialRecord
    {
        public string rendererHierarchyPath;
        public string rendererGlobalObjectId;
        public int submeshIndex;
        public Material originalMaterial;
        public Material generatedMaterial;
        public string[] generatedTexturePaths;
    }

    /// <summary>
    /// Persistent ScriptableObject store tracking original hair materials
    /// so Hair Gloss Studio changes remain 100% non-destructive and reversible.
    /// </summary>
    public class HairGlossBackupStore : ScriptableObject
    {
        public const string DefaultStoreAssetPath = "Assets/HairGlossStudio_Generated/HairGlossBackupStore.asset";

        [SerializeField]
        private List<RendererMaterialRecord> records = new List<RendererMaterialRecord>();

        public IReadOnlyList<RendererMaterialRecord> Records => records;

        public static HairGlossBackupStore LoadOrCreate(string storeAssetPath = DefaultStoreAssetPath)
        {
            HairGlossBackupStore store = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(storeAssetPath);
            if (store != null)
            {
                return store;
            }

            string folder = System.IO.Path.GetDirectoryName(storeAssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
            {
                HairTextureGenerator.EnsureFolderExists(folder);
            }

            store = CreateInstance<HairGlossBackupStore>();
            AssetDatabase.CreateAsset(store, storeAssetPath);
            AssetDatabase.SaveAssets();
            return store;
        }

        public void RegisterBackup(
            Renderer renderer,
            int submeshIndex,
            Material originalMaterial,
            Material generatedMaterial,
            string[] generatedTexturePaths)
        {
            if (renderer == null || originalMaterial == null)
            {
                return;
            }

            string globalId = GetGlobalIdString(renderer);
            string hierarchyPath = GetHierarchyPath(renderer.transform);

            if (TryFindRecordIndex(globalId, hierarchyPath, submeshIndex, out int existingIndex))
            {
                RendererMaterialRecord existing = records[existingIndex];
                // Preserve the earliest true original material if re-applying gloss
                if (existing.originalMaterial == null)
                {
                    existing.originalMaterial = originalMaterial;
                }
                existing.rendererGlobalObjectId = globalId;
                existing.rendererHierarchyPath = hierarchyPath;
                existing.generatedMaterial = generatedMaterial;
                existing.generatedTexturePaths = generatedTexturePaths ?? Array.Empty<string>();
            }
            else
            {
                records.Add(new RendererMaterialRecord
                {
                    rendererHierarchyPath = hierarchyPath,
                    rendererGlobalObjectId = globalId,
                    submeshIndex = submeshIndex,
                    originalMaterial = originalMaterial,
                    generatedMaterial = generatedMaterial,
                    generatedTexturePaths = generatedTexturePaths ?? Array.Empty<string>()
                });
            }

            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }

        public bool TryGetRecord(Renderer renderer, int submeshIndex, out RendererMaterialRecord record)
        {
            record = null;
            if (renderer == null)
            {
                return false;
            }

            string globalId = GetGlobalIdString(renderer);
            string hierarchyPath = GetHierarchyPath(renderer.transform);

            if (TryFindRecordIndex(globalId, hierarchyPath, submeshIndex, out int index))
            {
                record = records[index];
                return true;
            }

            return false;
        }

        public Material GetOriginalMaterial(Renderer renderer, int submeshIndex)
        {
            if (TryGetRecord(renderer, submeshIndex, out RendererMaterialRecord record) && record.originalMaterial != null)
            {
                return record.originalMaterial;
            }

            if (renderer != null)
            {
                Material[] mats = renderer.sharedMaterials;
                if (mats != null && submeshIndex >= 0 && submeshIndex < mats.Length)
                {
                    return mats[submeshIndex];
                }
            }

            return null;
        }

        public bool HasBackupForRoot(GameObject rootObject)
        {
            if (rootObject == null)
            {
                return false;
            }

            var renderers = rootObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null) continue;

                for (int i = 0; i < mats.Length; i++)
                {
                    if (TryGetRecord(renderer, i, out RendererMaterialRecord rec) && rec.originalMaterial != null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool RestoreTarget(Renderer renderer, int submeshIndex, bool deleteGeneratedAssets = false)
        {
            if (renderer == null)
            {
                return false;
            }

            string globalId = GetGlobalIdString(renderer);
            string hierarchyPath = GetHierarchyPath(renderer.transform);

            if (!TryFindRecordIndex(globalId, hierarchyPath, submeshIndex, out int index))
            {
                return false;
            }

            RendererMaterialRecord record = records[index];
            if (record.originalMaterial == null)
            {
                return false;
            }

            Material[] sharedMats = renderer.sharedMaterials;
            if (sharedMats == null || submeshIndex < 0 || submeshIndex >= sharedMats.Length)
            {
                return false;
            }

            Undo.RecordObject(renderer, "Hair Gloss Studio Revert Material");
            sharedMats[submeshIndex] = record.originalMaterial;
            renderer.sharedMaterials = sharedMats;
            EditorUtility.SetDirty(renderer);

            if (deleteGeneratedAssets)
            {
                CleanupGeneratedAssets(record, index);
            }

            records.RemoveAt(index);
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
            return true;
        }

        public int RestoreAllUnderRoot(GameObject rootObject, bool deleteGeneratedAssets = false)
        {
            if (rootObject == null)
            {
                return 0;
            }

            int restoredCount = 0;
            var renderers = rootObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null) continue;

                for (int i = 0; i < mats.Length; i++)
                {
                    if (RestoreTarget(renderer, i, deleteGeneratedAssets))
                    {
                        restoredCount++;
                    }
                }
            }

            return restoredCount;
        }

        private void CleanupGeneratedAssets(RendererMaterialRecord record, int skipIndex)
        {
            // Only delete generated assets if no remaining record still references them
            bool matInUse = false;
            for (int i = 0; i < records.Count; i++)
            {
                if (i == skipIndex) continue;
                if (records[i].generatedMaterial == record.generatedMaterial)
                {
                    matInUse = true;
                    break;
                }
            }

            if (!matInUse && record.generatedMaterial != null)
            {
                string matPath = AssetDatabase.GetAssetPath(record.generatedMaterial);
                if (!string.IsNullOrEmpty(matPath))
                {
                    AssetDatabase.DeleteAsset(matPath);
                }
            }

            if (record.generatedTexturePaths == null)
            {
                return;
            }

            foreach (string texPath in record.generatedTexturePaths)
            {
                if (string.IsNullOrEmpty(texPath)) continue;
                bool texInUse = false;
                for (int i = 0; i < records.Count; i++)
                {
                    if (i == skipIndex || records[i].generatedTexturePaths == null) continue;
                    foreach (string otherPath in records[i].generatedTexturePaths)
                    {
                        if (string.Equals(texPath, otherPath, StringComparison.OrdinalIgnoreCase))
                        {
                            texInUse = true;
                            break;
                        }
                    }
                    if (texInUse) break;
                }

                if (!texInUse)
                {
                    AssetDatabase.DeleteAsset(texPath);
                }
            }
        }

        private bool TryFindRecordIndex(string globalId, string hierarchyPath, int submeshIndex, out int index)
        {
            for (int i = 0; i < records.Count; i++)
            {
                RendererMaterialRecord r = records[i];
                if (r.submeshIndex != submeshIndex) continue;

                if (!string.IsNullOrEmpty(globalId) &&
                    string.Equals(r.rendererGlobalObjectId, globalId, StringComparison.Ordinal))
                {
                    index = i;
                    return true;
                }
            }

            for (int i = 0; i < records.Count; i++)
            {
                RendererMaterialRecord r = records[i];
                if (r.submeshIndex != submeshIndex) continue;

                if (!string.IsNullOrEmpty(hierarchyPath) &&
                    string.Equals(r.rendererHierarchyPath, hierarchyPath, StringComparison.Ordinal))
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        public static string GetGlobalIdString(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }
            return GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
        }

        public static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var stack = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack);
        }
    }
}
