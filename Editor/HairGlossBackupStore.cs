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
        public string generatedMaterialGuid;
        public string[] generatedTextureGuids;
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
        private static string[] cachedStoreGuids;

        [InitializeOnLoadMethod]
        private static void InitializeStoreCache()
        {
            EditorApplication.projectChanged -= InvalidateStoreCache;
            EditorApplication.projectChanged += InvalidateStoreCache;
        }

        private static void InvalidateStoreCache()
        {
            cachedStoreGuids = null;
        }

        public static HairGlossBackupStore FindForTarget(Renderer renderer, int submeshIndex)
        {
            return FindForTarget(renderer, submeshIndex, out _);
        }

        public static HairGlossBackupStore FindForTarget(Renderer renderer, int submeshIndex, out bool ambiguous)
        {
            ambiguous = false;
            if (renderer == null) return null;
            HairGlossBackupStore match = null;
            if (cachedStoreGuids == null) cachedStoreGuids = AssetDatabase.FindAssets("t:HairGlossBackupStore");
            foreach (string guid in cachedStoreGuids)
            {
                HairGlossBackupStore store = AssetDatabase.LoadAssetAtPath<HairGlossBackupStore>(AssetDatabase.GUIDToAssetPath(guid));
                if (store == null) continue;
                bool found = store.TryFindRecordIndex(GetGlobalIdString(renderer), GetHierarchyPath(renderer.transform), submeshIndex, out int index);
                if (index == -2 || (found && match != null))
                {
                    ambiguous = true;
                    return null;
                }
                if (!found) continue;
                match = store;
            }
            return match;
        }

        public bool OwnsGeneratedAssets(Renderer renderer, int submeshIndex)
        {
            if (!TryGetRecord(renderer, submeshIndex, out RendererMaterialRecord record) ||
                record.generatedMaterial == null || string.IsNullOrEmpty(record.generatedMaterialGuid) ||
                record.generatedTexturePaths == null || record.generatedTexturePaths.Length != 4 ||
                record.generatedTextureGuids == null || record.generatedTextureGuids.Length != 4)
            {
                return false;
            }

            if (AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(record.generatedMaterial)) != record.generatedMaterialGuid)
                return false;
            for (int i = 0; i < 4; i++)
            {
                if (string.IsNullOrEmpty(record.generatedTextureGuids[i]) ||
                    AssetDatabase.AssetPathToGUID(record.generatedTexturePaths[i]) != record.generatedTextureGuids[i])
                    return false;
            }
            return true;
        }

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
            InvalidateStoreCache();
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
            bool hasExisting = TryFindRecordIndex(globalId, hierarchyPath, submeshIndex, out int existingIndex);
            if (existingIndex == -2) throw new InvalidOperationException("Multiple hair gloss backups match this target.");
            Undo.RecordObject(this, "Hair Gloss Studio Backup");

            if (hasExisting)
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

            if (TryGetRecord(renderer, submeshIndex, out RendererMaterialRecord registered))
            {
                registered.generatedMaterialGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(generatedMaterial));
                registered.generatedTextureGuids = new string[registered.generatedTexturePaths.Length];
                for (int i = 0; i < registered.generatedTexturePaths.Length; i++)
                    registered.generatedTextureGuids[i] = AssetDatabase.AssetPathToGUID(registered.generatedTexturePaths[i]);
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
            Undo.RecordObject(this, "Hair Gloss Studio Revert Backup");
            sharedMats[submeshIndex] = record.originalMaterial;
            renderer.sharedMaterials = sharedMats;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
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
            index = -1;
            for (int i = 0; i < records.Count; i++)
            {
                RendererMaterialRecord r = records[i];
                if (r.submeshIndex != submeshIndex) continue;

                if (!string.IsNullOrEmpty(globalId) &&
                    string.Equals(r.rendererGlobalObjectId, globalId, StringComparison.Ordinal))
                {
                    if (index >= 0) { index = -2; return false; }
                    index = i;
                }
            }
            if (index >= 0) return true;

            for (int i = 0; i < records.Count; i++)
            {
                RendererMaterialRecord r = records[i];
                if (r.submeshIndex != submeshIndex) continue;
                if (GlobalObjectId.TryParse(r.rendererGlobalObjectId, out GlobalObjectId storedId) &&
                    GlobalObjectId.GlobalObjectIdentifierToObjectSlow(storedId) != null) continue;

                if (!string.IsNullOrEmpty(hierarchyPath) &&
                    string.Equals(r.rendererHierarchyPath, hierarchyPath, StringComparison.Ordinal))
                {
                    if (index >= 0) { index = -2; return false; }
                    index = i;
                }
            }

            return index >= 0;
        }

        public static string GetGlobalIdString(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return id.identifierType == 0 || GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) != obj
                ? string.Empty : id.ToString();
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
                stack.Push($"{current.GetSiblingIndex()}:{current.name}");
                current = current.parent;
            }

            return $"{transform.gameObject.scene.path}|{string.Join("/", stack)}";
        }
    }
}
