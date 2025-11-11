using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class SpeedTrail : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] private PlayerController controller;          // assign in inspector or auto-find
        [SerializeField] private SkinnedMeshRenderer playerMesh;       // WORLD/3P mesh (not FP arms)

        [Header("Afterimage Settings")]
        [SerializeField] private float minMultiplierForTrail = 1.5f;
        [SerializeField] private float spawnInterval = 0.05f;
        [SerializeField] private float ghostLifetime = 0.30f;
        [SerializeField] private float spawnOffset = 0.5f; 
        [SerializeField] private Material ghostMaterial;
        [SerializeField] private Color trailColor = new(0.3f, 0.7f, 1f, 0.5f);

        private float _lastSpawnTime;
        private readonly Queue<GameObject> _ghostPool = new();
        private const int PoolSize = 20;
        private Vector3 _lastPosition;

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
        
            _lastPosition = transform.position;
            if(ghostMaterial == null) CreateGhostMaterial();
            for(var i = 0; i < PoolSize; i++) CreateGhost();
        }

        private void Update() {
            return;
            if(IsOwner) return;
            if(!playerMesh) return;

            // Compute a multiplier from velocity so it works on ALL clients.
            // Match your weapon’s thresholds for feel.
            var speed = controller ? controller.CurrentFullVelocity.magnitude : 0f;
            var targetMul = 1f;
            const float minSpeed = Weapons.Weapon.MinSpeedThreshold; // 15f
            const float maxSpeed = Weapons.Weapon.MaxSpeedThreshold;
            if(speed > minSpeed) {
                var t = Mathf.InverseLerp(minSpeed, maxSpeed, speed);
                targetMul = Mathf.Lerp(1f,  controller ? controller.GetComponent<WeaponManager>()?.CurrentWeapon?.maxDamageMultiplier ?? 2f : 2f, t);
            }

            if(targetMul < minMultiplierForTrail) return;

            // Faster -> more frequent
            var speedFactor = Mathf.InverseLerp(minMultiplierForTrail, 3f, targetMul);
            var adjustedInterval = Mathf.Lerp(spawnInterval * 2f, spawnInterval * 0.5f, speedFactor);

            if(Time.time - _lastSpawnTime < adjustedInterval) return;
            if(!controller.IsOwner) {
                SpawnAfterimage();
                _lastSpawnTime = Time.time;
            }
        }

        private GameObject CreateGhost() {
            var ghost = new GameObject("AfterimageGhost");
            // Layer decided per-viewer:
            if(controller) {
                if(controller.IsOwner) {
                    ghost.layer = LayerMask.NameToLayer("Masked");
                } else {
                    ghost.layer = LayerMask.NameToLayer("Default");
                }
            } else {
                Debug.LogWarning("SpeedTrail: No PlayerController found on the object.");
                ghost.layer = LayerMask.NameToLayer("Masked");
            }

            ghost.SetActive(false);
            ghost.AddComponent<MeshFilter>();
            var mr = ghost.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _ghostPool.Enqueue(ghost);
            return ghost;
        }

        private void SpawnAfterimage() {
            var ghost = _ghostPool.FirstOrDefault(g => g && !g.activeInHierarchy) ?? CreateGhost();

            // Movement direction
            var moveDir = (transform.position - _lastPosition);
            if(moveDir.sqrMagnitude < 0.0001f) moveDir = -transform.forward;
            else moveDir.Normalize();

            var spawnPos = playerMesh.transform.position - moveDir * spawnOffset;

            ghost.transform.SetPositionAndRotation(spawnPos, playerMesh.transform.rotation);
            ghost.transform.localScale = playerMesh.transform.lossyScale;

            _lastPosition = transform.position;

            var baked = new Mesh();
            playerMesh.BakeMesh(baked);

            var mf = ghost.GetComponent<MeshFilter>();
            mf.sharedMesh = baked;

            var mr = ghost.GetComponent<MeshRenderer>();
            // material per instance so alpha fades independently
            mr.material = ghostMaterial ? new Material(ghostMaterial) : NewGhostMaterial();

            ghost.SetActive(true);
            StartCoroutine(FadeAndReturnGhost(ghost, mr));
        }

        private IEnumerator FadeAndReturnGhost(GameObject ghost, MeshRenderer mr) {
            var t = 0f;
            var mat = mr.material;
            var c0 = mat.color;
            while(t < ghostLifetime) {
                t += Time.deltaTime;
                var a = Mathf.Lerp(c0.a, 0f, t / ghostLifetime);
                mat.color = new Color(c0.r, c0.g, c0.b, a);
                yield return null;
            }
            ghost.SetActive(false);
            Destroy(mat); // destroy the instance
            _ghostPool.Enqueue(ghost);
        }

        private void CreateGhostMaterial() {
            ghostMaterial = NewGhostMaterial();
        }

        private Material NewGhostMaterial() {
            var m = new Material(Shader.Find("Standard"));
            m.SetFloat(Shader.PropertyToID("Mode"), 3);
            m.SetInt(Shader.PropertyToID("SrcBlend"), (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt(Shader.PropertyToID("DstBlend"), (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt(Shader.PropertyToID("ZWrite"), 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 3000;
            m.color = trailColor;
            return m;
        }
    }
}