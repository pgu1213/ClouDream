using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;

namespace ClouDream.Editor
{
    [CustomEditor(typeof(CloudSeaWorld))]
    public sealed class CloudSeaWorldEditor : UnityEditor.Editor
    {

        /// <summary>구름 설정과 맵 저장 버튼을 Inspector에 표시합니다.</summary>
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Geometry/seed/coverage/tower changes need Bake Maps. Surface controls update immediately. The generated lower layer is deliberately continuous.", MessageType.Info);
            DrawDefaultInspector();
            string buttonLabel = "Bake and save maps";
            if (Application.isPlaying)
            {
                buttonLabel = "Regenerate preview maps";
            }

            if (GUILayout.Button(buttonLabel))
            {
                var world = (CloudSeaWorld)target;
                if (Application.isPlaying)
                {
                    world.RebuildMaps();
                    return;
                }

                Bake(world);
            }
        }

        /// <summary>생성된 맵을 프로젝트 에셋으로 저장하고 장면에 연결합니다.</summary>
        public static void Bake(CloudSeaWorld world)
        {
            world.weatherMap = Store(world.GenerateWeatherMap(), world.weatherMap, "WeatherMap");
            world.heightLut = Store(world.GenerateHeightLut(), world.heightLut, "HeightLUT");
            world.Apply();
            EditorUtility.SetDirty(world);
            EditorUtility.SetDirty(world.GetComponent<Volume>().sharedProfile);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            AssetDatabase.SaveAssets();
        }

        /// <summary>기존 텍스처 에셋을 갱신하거나 새 에셋을 생성합니다.</summary>
        static Texture2D Store(Texture2D generated, Texture2D existing, string name)
        {
            if (existing == null || !AssetDatabase.Contains(existing))
            {
                if (!AssetDatabase.IsValidFolder("Assets/CloudSea/Textures"))
                {
                    AssetDatabase.CreateFolder("Assets/CloudSea", "Textures");
                }

                AssetDatabase.CreateAsset(generated, AssetDatabase.GenerateUniqueAssetPath("Assets/CloudSea/Textures/" + name + ".asset"));
                return generated;
            }

            Undo.RegisterCompleteObjectUndo(existing, "Bake cloud maps");
            EditorUtility.CopySerialized(generated, existing);
            existing.name = System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(existing));
            existing.Apply();
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(generated);
            return existing;
        }

        /// <summary>변경 사항 저장을 확인한 뒤 기존 HDRP 운해 장면을 엽니다.</summary>
        [MenuItem("ClouDream/Open Cloud Sea")]
        static void Open()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                EditorSceneManager.OpenScene("Assets/CloudSea/Scenes/CloudSea.unity");
            }
        }
    }
}
