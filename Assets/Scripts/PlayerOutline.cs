using UnityEngine;

[RequireComponent(typeof(SkinnedMeshRenderer))]
public class PlayerOutline : MonoBehaviour
{
    [Header("Outline Settings")]
    [Range(0f, 0.02f)]
    public float outlineThickness = 0.005f;
    public Color outlineColor = Color.black;

    [Header("Team Colors")]
    public bool useTeamColor = false;
    public Color teamColor = Color.blue; // Default teammate
    public Color enemyColor = Color.red;

    private Material outlineMaterial;
    private SkinnedMeshRenderer skinnedRenderer;
    private MaterialPropertyBlock propertyBlock;

    private void Awake()
    {
        skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
        propertyBlock = new MaterialPropertyBlock();

        // Create instance of outline material
        outlineMaterial = new Material(Shader.Find("Custom/ToonOutline"));
        outlineMaterial.hideFlags = HideFlags.HideAndDontSave;
    }

    private void Update()
    {
        if(Camera.main != null) {
            float distance = Vector3.Distance(Camera.main.transform.position, transform.position);
            outlineThickness = Mathf.Lerp(0.002f, 0.008f, distance / 50f);
        }

        UpdateOutline();
    }

    private void UpdateOutline()
    {
        if (skinnedRenderer == null || outlineMaterial == null) return;

        // Apply to all materials (we inject via property block)
        skinnedRenderer.GetPropertyBlock(propertyBlock);

        propertyBlock.SetColor("_OutlineColor", outlineColor);
        propertyBlock.SetFloat("_OutlineThickness", outlineThickness);
        propertyBlock.SetFloat("_UseTeamColor", useTeamColor ? 1f : 0f);
        propertyBlock.SetColor("_TeamColor", useTeamColor ? teamColor : Color.white);

        skinnedRenderer.SetPropertyBlock(propertyBlock);

        // Assign material (only once)
        if (skinnedRenderer.sharedMaterial == null || 
            skinnedRenderer.sharedMaterial.shader != outlineMaterial.shader)
        {
            skinnedRenderer.sharedMaterial = outlineMaterial;
        }
    }

    // === PUBLIC API FOR TEAM SYSTEM ===
    public void SetTeam(bool isTeammate)
    {
        useTeamColor = true;
        teamColor = isTeammate ? Color.blue : enemyColor;
    }

    public void ClearTeam()
    {
        useTeamColor = false;
    }
}