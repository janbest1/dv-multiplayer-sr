// Editor helper for importing the generated Vectron FBX into a Custom Car
// Loader project.
//
// Blender cannot give two sibling objects the same name, so the exported FBX
// contains "[axle].001" style names where CCL expects a plain "[axle]".
// This also turns the exported collider proxy boxes into real BoxColliders.
//
// Put this file anywhere under Assets/Editor/ in your CCL Unity project,
// select the imported model in the hierarchy and run
// Tools > Vectron > Prepare imported model.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class VectronCclSetup
{
    static readonly Regex BlenderSuffix = new Regex(@"\.\d{3}$");

    static readonly HashSet<string> ProxyColliders = new HashSet<string>
    {
        "[collision]", "[walkable]", "[items]", "[camera dampening]"
    };

    [MenuItem("Tools/Vectron/Prepare imported model")]
    static void Prepare()
    {
        var root = Selection.activeGameObject;
        if (root == null)
        {
            EditorUtility.DisplayDialog("Vectron",
                "Select the imported Vectron model in the hierarchy first.", "OK");
            return;
        }

        int renamed = 0, colliders = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            var clean = BlenderSuffix.Replace(t.name, "");
            if (clean != t.name && (clean.StartsWith("[") || clean == "bogie_car"
                || clean.StartsWith("buffer anchor") || clean == "BuffersAndChainRig"))
            {
                Undo.RecordObject(t.gameObject, "Rename for CCL");
                t.name = clean;
                renamed++;
            }

            if (ProxyColliders.Contains(t.name) && t.GetComponent<BoxCollider>() == null)
            {
                var mf = t.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var box = Undo.AddComponent<BoxCollider>(t.gameObject);
                    box.center = mf.sharedMesh.bounds.center;
                    box.size = mf.sharedMesh.bounds.size;
                    var mr = t.GetComponent<MeshRenderer>();
                    if (mr != null) Undo.DestroyObjectImmediate(mr);
                    colliders++;
                }
            }
        }

        Debug.Log($"[Vectron] renamed {renamed} objects, created {colliders} box colliders.\n" +
                  "Check that BogieF/BogieR sit at y = 0 and the coupler rigs at y = 1.05, " +
                  "then run the CCL car validator.");
    }
}
#endif
