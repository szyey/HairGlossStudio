using System;
using System.Collections.Generic;
using UnityEngine;

namespace HairGlossStudio.Editor
{
    [Serializable]
    public class HairMaterialTarget
    {
        public Renderer renderer;
        public Mesh mesh;
        public int submeshIndex;
        public Material currentMaterial;
        public Material originalMaterial;
        public bool isSelected;
        public bool isLilToon;
        public bool isHairCandidate;

        public string DisplayName
        {
            get
            {
                string rendererName = renderer != null ? renderer.name : "Missing Renderer";
                string matName = currentMaterial != null ? currentMaterial.name : "Missing Material";
                return $"{rendererName}  [{submeshIndex}: {matName}]";
            }
        }
    }

    /// <summary>
    /// Detects hair mesh renderers and lilToon materials under a target avatar or hair GameObject.
    /// </summary>
    public static class HairTargetDetector
    {
        private static readonly string[] HairKeywords =
        {
            "hair",
            "kami",
            "bang",
            "front",
            "back",
            "side",
            "twin",
            "ponytail",
            "ahoge",
            "エクステ",
            "髪",
            "前髪",
            "後髪",
            "横髪"
        };

        public static List<HairMaterialTarget> DetectTargets(GameObject rootObject, bool includeAllRenderers = false)
        {
            var results = new List<HairMaterialTarget>();
            if (rootObject == null)
            {
                return results;
            }

            var allEntries = new List<HairMaterialTarget>();
            bool anyHairMatched = false;

            var renderers = rootObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                Mesh mesh = ExtractMesh(renderer);
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer))
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    continue;
                }

                bool rendererOrMeshMatches = ContainsHairKeyword(renderer.name) ||
                                             (mesh != null && ContainsHairKeyword(mesh.name));

                for (int i = 0; i < materials.Length; i++)
                {
                    Material mat = materials[i];
                    if (mat == null)
                    {
                        continue;
                    }

                    bool matMatches = ContainsHairKeyword(mat.name);
                    bool isHair = rendererOrMeshMatches || matMatches;
                    if (isHair)
                    {
                        anyHairMatched = true;
                    }

                    bool isLilToon = IsLilToonShader(mat.shader);

                    var target = new HairMaterialTarget
                    {
                        renderer = renderer,
                        mesh = mesh,
                        submeshIndex = i,
                        currentMaterial = mat,
                        originalMaterial = mat,
                        isHairCandidate = isHair,
                        isLilToon = isLilToon,
                        isSelected = isHair && isLilToon
                    };

                    allEntries.Add(target);
                }
            }

            if (!anyHairMatched)
            {
                // User likely passed a custom-named hair object directly.
                foreach (HairMaterialTarget entry in allEntries)
                {
                    entry.isHairCandidate = true;
                    entry.isSelected = entry.isLilToon;
                    results.Add(entry);
                }
                return results;
            }

            foreach (HairMaterialTarget entry in allEntries)
            {
                if (includeAllRenderers || entry.isHairCandidate)
                {
                    results.Add(entry);
                }
            }

            return results;
        }

        public static bool IsLilToonShader(Shader shader)
        {
            if (shader == null || string.IsNullOrEmpty(shader.name))
            {
                return false;
            }

            return shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Mesh ExtractMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh;
            }

            if (renderer is MeshRenderer)
            {
                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }

            return null;
        }

        private static bool ContainsHairKeyword(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (int i = 0; i < HairKeywords.Length; i++)
            {
                if (name.IndexOf(HairKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
