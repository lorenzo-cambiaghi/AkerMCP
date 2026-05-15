using UnityEngine;
using UnityEditor;

namespace AkerMcp.Unity.Editor
{
    public static class TestSceneSetup
    {
        [MenuItem("AkerMcp/Setup Test Scene")]
        public static void SetupTestScene()
        {
            // Player with child camera and rigidbody
            var player = new GameObject("Player");
            player.AddComponent<Rigidbody>();
            player.transform.position = new Vector3(0, 1, 0);
            Undo.RegisterCreatedObjectUndo(player, "Create Player");

            var playerCamera = new GameObject("PlayerCamera");
            playerCamera.AddComponent<Camera>();
            playerCamera.transform.SetParent(player.transform);
            playerCamera.transform.localPosition = new Vector3(0, 2, -5);
            playerCamera.transform.localRotation = Quaternion.Euler(15, 0, 0);
            Undo.RegisterCreatedObjectUndo(playerCamera, "Create PlayerCamera");

            // Enemies at different positions
            for (int i = 0; i < 3; i++)
            {
                var enemy = GameObject.CreatePrimitive(PrimitiveType.Cube);
                enemy.name = $"Enemy_{i + 1}";
                enemy.transform.position = new Vector3(i * 3 - 3, 0.5f, 5);
                enemy.AddComponent<Rigidbody>();
                enemy.GetComponent<Renderer>().material.color = Color.red;
                Undo.RegisterCreatedObjectUndo(enemy, $"Create Enemy_{i + 1}");
            }

            // Ground plane
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(5, 1, 5);
            Undo.RegisterCreatedObjectUndo(ground, "Create Ground");

            // Point light
            var pointLight = new GameObject("PointLight");
            var light = pointLight.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 2;
            light.color = Color.yellow;
            pointLight.transform.position = new Vector3(0, 5, 0);
            Undo.RegisterCreatedObjectUndo(pointLight, "Create PointLight");

            // Empty parent for organization
            var props = new GameObject("Props");
            Undo.RegisterCreatedObjectUndo(props, "Create Props");

            var barrel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            barrel.name = "Barrel";
            barrel.transform.SetParent(props.transform);
            barrel.transform.position = new Vector3(-5, 0.5f, 0);
            Undo.RegisterCreatedObjectUndo(barrel, "Create Barrel");

            var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            crate.name = "Crate";
            crate.transform.SetParent(props.transform);
            crate.transform.position = new Vector3(5, 0.5f, 0);
            Undo.RegisterCreatedObjectUndo(crate, "Create Crate");

            Debug.Log("[AkerMcp] Test scene setup complete! Objects created: Player, 3 Enemies, Ground, PointLight, Props (Barrel, Crate)");
            EditorUtility.DisplayDialog("AkerMcp Test Scene",
                "Test scene objects created:\n\n" +
                "- Player (with PlayerCamera + Rigidbody)\n" +
                "- Enemy_1, Enemy_2, Enemy_3 (cubes with Rigidbody)\n" +
                "- Ground (plane)\n" +
                "- PointLight\n" +
                "- Props/Barrel, Props/Crate\n\n" +
                "Save the scene to Assets/Scenes/TestScene.unity",
                "OK");
        }
    }
}
