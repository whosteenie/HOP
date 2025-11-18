using System.Collections;
using Game.Player;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerController))]
public class PlayerTeamManager : NetworkBehaviour {
    // --------------------------------------------------------------------
    // 1. Networked team (synced once at spawn)
    // --------------------------------------------------------------------
    public NetworkVariable<SpawnPoint.Team> netTeam = new(
        SpawnPoint.Team.TeamA,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // --------------------------------------------------------------------
    // 2. Outline colours (tweak in inspector)
    // --------------------------------------------------------------------
    [Header("Outline Colours")] 
    [ColorUsage(true, true)] // HDR enabled: showAlpha=true, hdr=true
    [SerializeField] private Color teammateOutline = new(0f, 1.5f, 2.5f, 1f); // Bright cyan-blue with HDR glow

    [ColorUsage(true, true)] // HDR enabled
    [SerializeField] private Color enemyOutline = new(2.5f, 0.5f, 0.5f, 1f); // Bright red with HDR glow
    [ColorUsage(true, true)] // HDR enabled
    [SerializeField] private Color selfOutline = new(0f, 0f, 0f, 1f); // Black
    
    [Header("Outline Distance Scaling")]
    [SerializeField] private float minOutlineSize = 0.008f; // Minimum size (close distance)
    [SerializeField] private float maxOutlineSize = 0.025f; // Maximum size (far distance) - much thicker at distance
    [SerializeField] private float distanceScaleStart = 10f; // Start scaling at this distance
    [SerializeField] private float distanceScaleEnd = 100f; // Max scaling at this distance

    // --------------------------------------------------------------------
    // 3. Cached components
    // --------------------------------------------------------------------
    private SkinnedMeshRenderer _skinned;
    private MaterialPropertyBlock _propertyBlock;
    private PlayerController _pc;
    private Camera _mainCamera;

    // --------------------------------------------------------------------
    // Unity / Netcode lifecycle
    // --------------------------------------------------------------------
    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();

        _pc = GetComponent<PlayerController>();
        _skinned = GetComponentInChildren<SkinnedMeshRenderer>(true);
        _mainCamera = Camera.main;
        
        // Initialize MaterialPropertyBlock for per-instance properties
        if(_skinned != null) {
            _propertyBlock = new MaterialPropertyBlock();
            _skinned.GetPropertyBlock(_propertyBlock, 0); // Get existing properties for material index 0
        }

        // Subscribe to team changes (including late-joiners)
        // Note: netTeam is set by server during spawn, clients should not write to it
        netTeam.OnValueChanged -= OnTeamChanged;
        netTeam.OnValueChanged += OnTeamChanged;
        
        // Delay outline update to ensure all teams are synced
        StartCoroutine(DelayedOutlineUpdate());
    }

    private IEnumerator DelayedOutlineUpdate() {
        // Wait a frame to ensure network variables are synced
        yield return null;
        UpdateOutlineColour();
        
        // Also update when local player's team is set (if we're the local player)
        if(IsOwner) {
            // Wait a bit more for other players to sync
            yield return new WaitForSeconds(0.1f);
            // Update all other players' outlines when our team is set
            UpdateAllPlayerOutlines();
        }
    }

    private void UpdateAllPlayerOutlines() {
        // Find all other players and update their outlines
        var allPlayers = FindObjectsByType<PlayerTeamManager>(FindObjectsSortMode.None);
        foreach(var player in allPlayers) {
            if(player != this) {
                player.UpdateOutlineColour();
            }
        }
    }

    public override void OnNetworkDespawn() {
        netTeam.OnValueChanged -= OnTeamChanged;
        base.OnNetworkDespawn();
    }

    // --------------------------------------------------------------------
    // Called whenever NetTeam changes (including on spawn)
    // --------------------------------------------------------------------
    private void OnTeamChanged(SpawnPoint.Team previous, SpawnPoint.Team current) {
        UpdateOutlineColour();
        
        // If this is the local player's team changing, update all other players' outlines
        if(IsOwner) {
            UpdateAllPlayerOutlines();
        }
    }

    // --------------------------------------------------------------------
    // Helper: Check if current game mode is team-based
    // --------------------------------------------------------------------
    private static bool IsTeamBasedMode(string modeId) => modeId switch {
        "Team Deathmatch" => true,
        "CTF" => true,
        "Oddball" => true,
        "KOTH" => true,
        _ => false // Deathmatch, Private Match, etc. are FFA
    };

    // --------------------------------------------------------------------
    // Decide colour based on local player vs this player
    // Only updates in team-based modes; FFA modes leave outline unchanged
    // --------------------------------------------------------------------
    private void UpdateOutlineColour() {
        if(_skinned == null || _propertyBlock == null) return;

        // Check if current game mode is team-based
        var matchSettings = MatchSettings.Instance;
        bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

        // If FFA mode, don't update outline color (leave it as default/unchanged)
        if(!isTeamBased) {
            return;
        }

        // Team-based mode: update colors
        Color target;

        if(IsOwner) {
            // Yourself: don't change (leave as default/unchanged)
            return;
        } else {
            // Get the LOCAL player's team (only exists on clients)
            var localPlayer = NetworkManager.Singleton.LocalClient?.PlayerObject;
            if(localPlayer != null) {
                var localTeamMgr = localPlayer.GetComponent<PlayerTeamManager>();
                if(localTeamMgr != null && netTeam.Value == localTeamMgr.netTeam.Value) {
                    target = teammateOutline; // Same team → blue
                } else {
                    target = enemyOutline; // Different team → red
                }
            } else {
                target = enemyOutline; // Fallback (shouldn't happen)
            }
        }

        // Calculate distance-based outline size for better visibility at distance
        float outlineSize = CalculateOutlineSize();
        
        // Use MaterialPropertyBlock to set per-instance property without modifying shared material
        _propertyBlock.SetColor("_OutlineColor", target);
        _propertyBlock.SetFloat("_Size", outlineSize);
        _skinned.SetPropertyBlock(_propertyBlock, 0); // Apply to material index 0 (outline material)
    }
    
    // --------------------------------------------------------------------
    // Calculate outline size based on distance (larger at distance for visibility)
    // --------------------------------------------------------------------
    private float CalculateOutlineSize() {
        if(_mainCamera == null || _skinned == null) {
            _mainCamera = Camera.main;
            if(_mainCamera == null) return minOutlineSize;
        }
        
        // Get distance from camera to player
        float distance = Vector3.Distance(_mainCamera.transform.position, transform.position);
        
        // Clamp distance to scaling range
        float normalizedDistance = Mathf.InverseLerp(distanceScaleStart, distanceScaleEnd, distance);
        normalizedDistance = Mathf.Clamp01(normalizedDistance);
        
        // Interpolate between min and max size
        float size = Mathf.Lerp(minOutlineSize, maxOutlineSize, normalizedDistance);
        
        return size;
    }
    
    // --------------------------------------------------------------------
    // Update outline size every frame for distance-based scaling
    // --------------------------------------------------------------------
    private void Update() {
        if(_skinned == null || _propertyBlock == null) return;
        if(IsOwner) return; // Don't update for self
        
        // Check if current game mode is team-based
        var matchSettings = MatchSettings.Instance;
        bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);
        
        if(!isTeamBased) return;
        
        // Update outline size based on distance
        float outlineSize = CalculateOutlineSize();
        _skinned.GetPropertyBlock(_propertyBlock, 0);
        _propertyBlock.SetFloat("_Size", outlineSize);
        _skinned.SetPropertyBlock(_propertyBlock, 0);
    }
}