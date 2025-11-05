using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SpeedTrail : MonoBehaviour
{
    [Header("Shader Property IDs")]
    private static readonly int Mode = Shader.PropertyToID("_Mode");
    private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
    private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
    
    [Header("References")]
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private SkinnedMeshRenderer playerMesh;
    
    [Header("Afterimage Settings")]
    [SerializeField] private float minMultiplierForTrail = 1.5f;
    [SerializeField] private float spawnInterval = 0.05f;
    [SerializeField] private float ghostLifetime = 0.3f;
    [SerializeField] private float spawnOffset = 0.5f; // How far behind player to spawn
    [SerializeField] private Material ghostMaterial;
    [SerializeField] private Color trailColor = new Color(0.3f, 0.7f, 1f, 0.5f);
    
    private float _lastSpawnTime;
    private readonly Queue<GameObject> _ghostPool = new Queue<GameObject>();
    private const int PoolSize = 20;
    private Vector3 _lastPosition;

    private void Start()
    {
        _lastPosition = transform.position;
        
        // Create ghost material if not assigned
        if(ghostMaterial == null)
        {
            CreateGhostMaterial();
        }
        
        // Pre-populate ghost pool
        for(var i = 0; i < PoolSize; i++)
        {
            CreateGhost();
        }
    }
    
    private void Update()
    {
        var weapon = weaponManager.CurrentWeapon;
        var multiplier = weapon.CurrentDamageMultiplier;
        
        // If below threshold, fade out all active ghosts
        if(multiplier < minMultiplierForTrail)
        {
            // FadeOutAllGhosts();
            return;
        }
        
        // Calculate spawn rate based on speed (faster = more frequent)
        var speedFactor = Mathf.InverseLerp(minMultiplierForTrail, weapon.maxDamageMultiplier, multiplier);
        var adjustedInterval = Mathf.Lerp(spawnInterval * 2f, spawnInterval * 0.5f, speedFactor);

        if(!(Time.time - _lastSpawnTime >= adjustedInterval)) return;
        
        SpawnAfterimage();
        _lastSpawnTime = Time.time;
    }
    
    private void FadeOutAllGhosts()
    {
        // Find all active ghosts and deactivate them
        foreach(var ghost in _ghostPool)
        {
            if(ghost.activeInHierarchy)
            {
                ghost.SetActive(false);
            }
        }
    }
    
    private void CreateGhostMaterial()
    {
        ghostMaterial = new Material(Shader.Find("Standard"));
        ghostMaterial.SetFloat(Mode, 3);
        ghostMaterial.SetInt(SrcBlend, (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        ghostMaterial.SetInt(DstBlend, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        ghostMaterial.SetInt(ZWrite, 0);
        ghostMaterial.DisableKeyword("_ALPHATEST_ON");
        ghostMaterial.EnableKeyword("_ALPHABLEND_ON");
        ghostMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        ghostMaterial.renderQueue = 3000;
        ghostMaterial.color = trailColor;
    }
    
    private GameObject CreateGhost()
    {
        var ghost = new GameObject("AfterimageGhost") {
            layer = LayerMask.NameToLayer("Masked")
        };
        ghost.SetActive(false);
        
        var mf = ghost.AddComponent<MeshFilter>();
        var mr = ghost.AddComponent<MeshRenderer>();
        
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        
        _ghostPool.Enqueue(ghost);
        return ghost;
    }
    
    private void SpawnAfterimage()
    {
        // Get ghost from pool
        var ghost = _ghostPool.FirstOrDefault(g => !g.activeInHierarchy);

        if(ghost == null)
        {
            ghost = CreateGhost();
        }

        // Calculate movement direction
        Vector3 movementDirection = (transform.position - _lastPosition).normalized;
        
        // If no movement, use backward direction
        if(movementDirection == Vector3.zero)
        {
            movementDirection = -transform.forward;
        }
        
        // Spawn ghost behind the player based on movement direction
        Vector3 spawnPosition = playerMesh.transform.position - movementDirection * spawnOffset;
        
        // Position and setup ghost
        ghost.transform.position = spawnPosition;
        ghost.transform.rotation = playerMesh.transform.rotation;
        ghost.transform.localScale = playerMesh.transform.lossyScale;
        
        // Update last position for next frame
        _lastPosition = transform.position;
        
        // Bake mesh from skinned mesh renderer
        var bakedMesh = new Mesh();
        playerMesh.BakeMesh(bakedMesh);
        
        var mf = ghost.GetComponent<MeshFilter>();
        mf.mesh = bakedMesh;
        
        var mr = ghost.GetComponent<MeshRenderer>();
        mr.material = ghostMaterial;
        
        ghost.SetActive(true);
        
        // Start fade coroutine
        StartCoroutine(FadeAndReturnGhost(ghost, mr));
    }
    
    private IEnumerator FadeAndReturnGhost(GameObject ghost, MeshRenderer ghostRenderer)
    {
        var elapsed = 0f;
        var instanceMat = ghostRenderer.material;
        var startColor = trailColor;
        
        while(elapsed < ghostLifetime)
        {
            elapsed += Time.deltaTime;
            var alpha = Mathf.Lerp(startColor.a, 0f, elapsed / ghostLifetime);
            instanceMat.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null;
        }
        
        // Return to pool
        ghost.SetActive(false);
        Destroy(instanceMat);
        _ghostPool.Enqueue(ghost);
    }
}