using UnityEngine;

namespace Game.Match {
    [RequireComponent(typeof(ParticleSystem))]
    public class ZoneParticleSetup : MonoBehaviour {
        [Header("Zone Settings")]
        [SerializeField] private float zoneRadius = 5f;
        [SerializeField] private Color zoneColor = new(0, 0.5f, 1f, 0.05f); // Very low alpha for stacking
        [SerializeField] private Material particleMaterial; // User can assign "Default-Particle" or Smoke here

        [ContextMenu("Setup Particles")]
        public void Setup() {
            var ps = GetComponent<ParticleSystem>();
            var main = ps.main;
            var emission = ps.emission;
            var shape = ps.shape;
            var colorOverLifetime = ps.colorOverLifetime;
            var sizeOverLifetime = ps.sizeOverLifetime;
            var particleSystemRenderer = GetComponent<ParticleSystemRenderer>();

            // 1. Main Settings (Fog Mode)
            main.duration = 5f;
            main.loop = true;
            main.startLifetime = 1.5f; // Short life keeps them close to home
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.1f); // Tiny drift
            
            // Revert to complex size for Horizontal Billboards
            main.startSize3D = false;
            main.startSize = 2.5f; // Large puffs for smoothness
            
            main.startColor = zoneColor;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 10000; // Massive cap for seamless fog

            // 2. Emission (Explosive Density)
            emission.rateOverTime = 1000f; // No gaps allowed

            // 3. Shape (The Ring)
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = zoneRadius;
            shape.radiusThickness = 0f; 
            shape.rotation = new Vector3(-90f, 0f, 0f); 

            // 4. Color over Lifetime (Fade In / Fade Out)
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] { new(zoneColor, 0f), new(zoneColor, 1f) },
                new GradientAlphaKey[] { new(0f, 0f), new(1f, 0.2f), new(0f, 1f) }
            );
            colorOverLifetime.color = gradient;

            // 5. Rotation (Randomize + Spin)
            // Spinning helps blend the edges so they don't look like static stamp circles
            var mainRotation = ps.rotationOverLifetime; 
            mainRotation.enabled = true;
            mainRotation.z = new ParticleSystem.MinMaxCurve(-45f, 45f); // Slow spin
            
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f);

            // 6. Velocity (Gentle Rise only)
            var velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = false; 
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.1f); // Match main speed

            // 7. Size
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.8f), 
                new Keyframe(1f, 1.2f)
            ));

            // 8. Renderer (Horizontal Billboard)
            // This makes particles lie FLAT on the ground. 
            // They will never "stick out" towards the camera because they are flush with the floor.
            particleSystemRenderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            particleSystemRenderer.lengthScale = 1.0f; // Reset scale
            
            if (particleMaterial != null) {
                particleSystemRenderer.material = particleMaterial;
            } else {
                 Debug.Log("Assign a Material to avoid pink squares.");
            }
        }
    }
}
