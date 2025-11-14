using Game.Player;
using Network.Rpc;
using Unity.Netcode;
using UnityEngine;

public class DashController : NetworkBehaviour {
    [Header("References")] [SerializeField]
    private PlayerController playerController;

    [SerializeField] private CharacterController characterController;
    [SerializeField] private SwingGrapple swingGrapple;

    [Header("Dash Settings")] [SerializeField]
    [Range(8f, 15f)] private float dashSpeed = 2f;
    [SerializeField] private float dashDuration = 0.05f;
    [SerializeField] private float dashCooldown = 1.5f;

    public bool IsDashing { get; private set; }

    private NetworkVariable<bool> netIsDashing = new();
    private float dashTimer;
    private Vector3 dashVelocity;
    private Vector3 preDashHorizontalVelocity; // **NEW: Capture pre-dash momentum**
    private float dashCooldownTimer;
    private bool airDashPendingGround;

    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();
        netIsDashing.OnValueChanged += OnDashChanged;
    }

    public override void OnNetworkDespawn() {
        netIsDashing.OnValueChanged -= OnDashChanged;
    }

    [Rpc(SendTo.Server)]
    private void StartDashRpc(Vector3 direction) {
        if(netIsDashing.Value || dashCooldownTimer > 0) return;

        netIsDashing.Value = true;
        dashVelocity = direction * dashSpeed;
        preDashHorizontalVelocity = new Vector3(playerController.CurrentFullVelocity.x, 0f, playerController.CurrentFullVelocity.z); // **Capture!**
        dashTimer = 0f;
        airDashPendingGround = !playerController.IsGrounded;
    }

    private void OnDashChanged(bool _, bool dashing) {
        IsDashing = dashing;
    }

    private void TryDash(Vector2 moveInput) {
        if(!IsOwner || IsDashing || dashCooldownTimer > 0) return;

        if(moveInput.sqrMagnitude < 0.1f) return;

        var dashDir = (transform.forward * moveInput.y + transform.right * moveInput.x).normalized;
        dashDir.y = 0f;

        StartDashRpc(dashDir);
    }

    private void Update() {
        if(!IsOwner) return;

        dashCooldownTimer = Mathf.Max(0, dashCooldownTimer - Time.deltaTime);

        if(airDashPendingGround && playerController.IsGrounded) {
            airDashPendingGround = false;
            dashCooldownTimer = dashCooldown;
        }

        if(IsDashing) {
            dashTimer += Time.deltaTime;

            // **ADDITIVE: Current momentum + dash boost in input direction**
            Vector3 currentVel = playerController.CurrentFullVelocity;
            Vector3 dashDir = dashVelocity.normalized; // Direction only
            Vector3 boostedVel = currentVel + dashDir * dashSpeed;

            boostedVel.y = currentVel.y; // Keep vertical

            characterController.Move(boostedVel * Time.deltaTime);

            if(dashTimer >= dashDuration) {
                // **PRESERVE boosted momentum** - let friction/air strafe control it
                playerController.SetVelocity(new Vector3(boostedVel.x, 0f, boostedVel.z));
                EndDash();
            }
        }
    }

    private void EndDash() {
        IsDashing = false;
        netIsDashing.Value = false;

        if(!airDashPendingGround) {
            dashCooldownTimer = dashCooldown;
        }
    }

    public void OnDashInput() {
        TryDash(playerController.moveInput);
    }
}