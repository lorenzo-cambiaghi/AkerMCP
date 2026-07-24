using System;
using System.Collections.Generic;
using System.Linq;

namespace AkerMcp.Server
{
    /// <summary>
    /// A small catalog of vetted, ready-to-drop-in gameplay primitives, so the AI composes
    /// known-good behaviours instead of re-authoring error-prone scripts each time (and grinding
    /// the write_script -> refresh_scripts -> fix loop). Server-only: add_primitive writes the
    /// chosen variant via the existing write_script IPC. Per-engine variants; Unity primitives
    /// assume the legacy Input Manager is available (Active Input Handling = Both or Input Manager).
    /// </summary>
    public static class PrimitiveCatalog
    {
        public sealed class Primitive
        {
            public string Id = "";
            public string Summary = "";
            public string[] Fields = Array.Empty<string>();
            public string DefaultFile = "";        // relative to project (e.g. Assets/Scripts/X.cs)
            public Dictionary<string, string> Variants = new(StringComparer.OrdinalIgnoreCase); // engine -> source
        }

        public static IReadOnlyList<Primitive> All => _all;

        public static Primitive? Find(string id)
            => _all.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        // Match a get_project_info engine string ("Unity 6000…", "Godot…") to a variant key.
        public static string? EngineKey(string engineInfo)
        {
            var s = (engineInfo ?? "").ToLowerInvariant();
            if (s.Contains("unity")) return "unity";
            if (s.Contains("godot")) return "godot";
            if (s.Contains("stride")) return "stride";
            if (s.Contains("skelforge")) return "skelforge";
            return null;
        }

        private static readonly List<Primitive> _all = new()
        {
            new Primitive
            {
                Id = "platformer_controller_2d",
                Summary = "2D side-scroller movement: horizontal move + grounded jump (Rigidbody2D).",
                Fields = new[] { "moveSpeed", "jumpForce", "gravityScale", "groundTag", "jumpKey" },
                DefaultFile = "Assets/Scripts/PlatformerController2D.cs",
                Variants = { ["unity"] = @"using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class PlatformerController2D : MonoBehaviour
{
    public float moveSpeed = 7f;
    public float jumpForce = 12f;
    public float gravityScale = 4f;
    public string groundTag = ""Ground"";
    public KeyCode jumpKey = KeyCode.Space;

    Rigidbody2D _rb;
    int _groundContacts;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = gravityScale;
        _rb.freezeRotation = true;
    }

    void Update()
    {
        float x = Input.GetAxisRaw(""Horizontal"");
        _rb.velocity = new Vector2(x * moveSpeed, _rb.velocity.y);
        bool grounded = _groundContacts > 0;
        if (grounded && (Input.GetKeyDown(jumpKey) || Input.GetMouseButtonDown(0)))
            _rb.velocity = new Vector2(_rb.velocity.x, jumpForce);
    }

    void OnCollisionEnter2D(Collision2D c) { if (c.collider.CompareTag(groundTag)) _groundContacts++; }
    void OnCollisionExit2D(Collision2D c)  { if (c.collider.CompareTag(groundTag)) _groundContacts = Mathf.Max(0, _groundContacts - 1); }
}
" }
            },
            new Primitive
            {
                Id = "auto_runner_2d",
                Summary = "Auto-runner: constant rightward speed, jump on tap when grounded (Geometry-Dash/Flappy style).",
                Fields = new[] { "forwardSpeed", "jumpForce", "gravityScale", "groundTag", "jumpKey" },
                DefaultFile = "Assets/Scripts/AutoRunner2D.cs",
                Variants = { ["unity"] = @"using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class AutoRunner2D : MonoBehaviour
{
    public float forwardSpeed = 8f;
    public float jumpForce = 13f;
    public float gravityScale = 5f;
    public string groundTag = ""Ground"";
    public KeyCode jumpKey = KeyCode.Space;

    Rigidbody2D _rb;
    int _groundContacts;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = gravityScale;
        _rb.freezeRotation = true;
    }

    void FixedUpdate()
    {
        _rb.velocity = new Vector2(forwardSpeed, _rb.velocity.y);
    }

    void Update()
    {
        bool grounded = _groundContacts > 0;
        if (grounded && (Input.GetKeyDown(jumpKey) || Input.GetMouseButtonDown(0)))
            _rb.velocity = new Vector2(_rb.velocity.x, jumpForce);
    }

    void OnCollisionEnter2D(Collision2D c) { if (c.collider.CompareTag(groundTag)) _groundContacts++; }
    void OnCollisionExit2D(Collision2D c)  { if (c.collider.CompareTag(groundTag)) _groundContacts = Mathf.Max(0, _groundContacts - 1); }
}
" }
            },
            new Primitive
            {
                Id = "camera_follow_2d",
                Summary = "Smoothly follow a target on X (optionally Y) with an offset, in LateUpdate.",
                Fields = new[] { "target", "offset", "smooth", "followY" },
                DefaultFile = "Assets/Scripts/CameraFollow2D.cs",
                Variants = { ["unity"] = @"using UnityEngine;

public class CameraFollow2D : MonoBehaviour
{
    public Transform target;
    public Vector3 offset = new Vector3(2f, 0f, -10f);
    public float smooth = 8f;
    public bool followY = false;

    void LateUpdate()
    {
        if (target == null) return;
        float y = followY ? target.position.y + offset.y : transform.position.y;
        Vector3 goal = new Vector3(target.position.x + offset.x, y, offset.z);
        transform.position = Vector3.Lerp(transform.position, goal, 1f - Mathf.Exp(-smooth * Time.deltaTime));
    }
}
" }
            },
            new Primitive
            {
                Id = "killzone_2d",
                Summary = "Reload the scene when the object touches something tagged deadly (death + restart).",
                Fields = new[] { "deadlyTag", "reloadOnDeath" },
                DefaultFile = "Assets/Scripts/Killzone2D.cs",
                Variants = { ["unity"] = @"using UnityEngine;
using UnityEngine.SceneManagement;

public class Killzone2D : MonoBehaviour
{
    public string deadlyTag = ""Deadly"";
    public bool reloadOnDeath = true;

    void OnCollisionEnter2D(Collision2D c) { Hit(c.collider.tag); }
    void OnTriggerEnter2D(Collider2D c)    { Hit(c.tag); }

    void Hit(string t)
    {
        if (t == deadlyTag && reloadOnDeath)
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
" }
            },
            new Primitive
            {
                Id = "score_overlay",
                Summary = "Singleton score with an OnGUI top-center readout and AddScore(); no TextMeshPro needed.",
                Fields = new[] { "score", "label" },
                DefaultFile = "Assets/Scripts/ScoreOverlay.cs",
                Variants = { ["unity"] = @"using UnityEngine;

public class ScoreOverlay : MonoBehaviour
{
    public static ScoreOverlay Instance { get; private set; }
    public int score;
    public string label = ""Score"";

    void Awake() { Instance = this; }
    public void AddScore(int n = 1) { score += n; }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.UpperCenter };
        GUI.Label(new Rect(0, 12, Screen.width, 40), label + "": "" + score, style);
    }
}
" }
            },
        };
    }
}
