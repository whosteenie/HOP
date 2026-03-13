using System.Collections.Generic;
using System.Reflection;
using Game.Weapons.Manager;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public sealed partial class KinemationFpWeaponDriver : MonoBehaviour {
        [Header("KINEMATION")]
        [SerializeField] private GameObject fpsPlayerPrefab;
        [SerializeField] private GameObject weaponPrefab;
        [SerializeField] private bool disableKinemationWeaponSounds;
        [SerializeField] private bool disableKinemationPlayerSounds = true;
        [SerializeField] private bool routeWeaponSoundEventsToAudioService = true;
        [SerializeField] private bool disableKinemationInternalMuzzleFx = true;
        [SerializeField] private bool syncLookPitchWithPlayer;
        [SerializeField] private bool syncAirborneState;
        [SerializeField] private bool freezeLocomotionInAir = true;
        [SerializeField] private bool forceWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float sprintWalkGaitValue = 1.2f;
        [SerializeField, Range(0f, 1f)] private float equipUnlockNormalizedTime = 0.82f;

        [Header("Grapple")]
        [SerializeField] private bool enableRuntimeGrappleClavicleOffset;

        private GameObject _playerInstance;
        private FPSPlayerSettings _runtimePlayerSettings;
        private FPSPlayer _fpsPlayer;
        private Animator _fpsAnimator;
        private FPSWeapon _activeWeapon;
        private readonly KinemationActiveWeaponComponentCache _activeWeaponComponentCache = new();
        private Transform _muzzleTransform;
        private AudioSource _weaponAudioSource;
        private int _renderLayer = -1;
        private WeaponManager _weaponManager;
        // TODO(KIN-SPLIT): Extract reload/equip/Drake/Kar tracking into dedicated state objects.
        private bool _isTrackingReload;
        private bool _reloadHasBeenActive;
        private bool _reloadHasReceivedAnyEvent;
        private bool _reloadCompleteEventReceived;
        private bool _drakeCurrentReloadStartedEmpty;
        private bool _drakeCurrentEmptyReloadSawAmmoEject;
        private bool _drakeTopShellEjectedSinceReloadComplete;
        private bool _drakeShotCanceledReloadAfterAmmoEject;
        private bool _drakeShotCanceledEmptyReloadAfterAmmoEject;
        private bool _isTrackingEquip;
        private bool _equipHasBeenActive;
        private bool _equipCompleteEventReceived;
        private int _pendingReloadSingleEvents;
        private int _pendingWeaponFireSoundEvents;
        private readonly List<int> _pendingWeaponEventSoundIndices = new();
        private string _activeWeaponSoundKey = "unknown";
        private string _activeWeaponFireSoundId = "";
        private float _reloadTrackStartTime;
        private float _lastReloadSignalTime;
        private int _lastReloadSingleEventFrame = -1;
        private float _lastReloadSingleEventTime = -1f;
        private string _lastReloadSingleEventSource = "";
        private int _reloadSingleEventsReceivedDuringCurrentReload;
        private int _reloadSingleEventsConsumedDuringCurrentReload;
        private float _equipTrackStartTime;
        private float _lastEquipSignalTime;
        private bool _hasCachedWristDebugBones;
        private Transform _wristDebugUpperarmLeft;
        private Transform _wristDebugLowerarmLeft;
        private Transform _wristDebugTwistLeft;
        private Transform _wristDebugHandLeft;
        private Transform _clavicleLeft;
        private Transform _ikHandLeft;
        private Transform _grappleOrigin; // Optional empty child placed at desired palm position
        private bool _isRuntimeGrappleClavicleOffsetActive;
        private Vector3 _runtimeGrappleClavicleOffset;
        private int _runtimeGrappleOffsetWeaponIndex;
        private readonly HashSet<int> _suppressedMuzzleFxWeaponIds = new();
        private bool _suppressDrakeTopShellEjectOnNextReload;
        private bool _suppressDrakeBottomShellOnNextReload;
        private Transform _suppressedDrakeTopShellTransform;
        private Vector3 _suppressedDrakeTopShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeTopShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeTopShellOriginalLocalScale;
        private bool _hasSuppressedDrakeTopShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeTopShellRenderers;
        private bool[] _suppressedDrakeTopShellRendererEnabledStates;
        private bool _isDrakeTopShellSuppressionApplied;
        private Transform _suppressedDrakeBottomShellTransform;
        private Vector3 _suppressedDrakeBottomShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeBottomShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeBottomShellOriginalLocalScale;
        private bool _hasSuppressedDrakeBottomShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeBottomShellRenderers;
        private bool[] _suppressedDrakeBottomShellRendererEnabledStates;
        private bool _isDrakeBottomShellSuppressionApplied;
        private Transform _karLoopBulletTransform;
        private Vector3 _karLoopBulletOriginalLocalPosition;
        private bool _hasKarLoopBulletOriginalLocalPosition;
        private Vector3 _karLoopBulletOriginalLocalScale;
        private bool _hasKarLoopBulletOriginalLocalScale;
        private Renderer[] _karLoopBulletRenderers;
        private bool[] _karLoopBulletRendererEnabledStates;
        private bool _isKarLoopBulletHidden;
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;
        private const float EquipEnterGraceSeconds = 0.2f;
        private const float EquipSignalGraceSeconds = 0.05f;
        private const float DrakeTopShellHideOffset = 0.75f;
        private const float KarLoopBulletHideOffset = 0.55f;
        private static readonly int EquipHash = Animator.StringToHash("Equip");
        private static readonly int EquipOverrideHash = Animator.StringToHash("Equip_Override");

        private static readonly FieldInfo FpsPlayerMoveInputField =
            typeof(FPSPlayer).GetField("_moveInput", BindingFlags.Instance | BindingFlags.NonPublic);
        // TODO(KIN-SPLIT): Move reflective KIN/FPS private-member access behind a typed adapter.
        private static readonly FieldInfo FpsPlayerLookInputField =
            typeof(FPSPlayer).GetField("_lookInput", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerSprintingField =
            typeof(FPSPlayer).GetField("_bSprinting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerTacSprintingField =
            typeof(FPSPlayer).GetField("_bTacSprinting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo FpsPlayerSetMovementEnabledMethod =
            typeof(FPSPlayer).GetMethod("SetCharacterControllerMovementEnabled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerAllowControllerMovementField =
            typeof(FPSPlayer).GetField("allowCharacterControllerMovement", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponSoundAudioSourceField =
            typeof(FPSWeaponSound).GetField("_audioSource", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponActiveAmmoField =
            typeof(FPSWeapon).GetField("_activeAmmo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponIsReloadingField =
            typeof(FPSWeapon).GetField("_isReloading", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponIsFiringField =
            typeof(FPSWeapon).GetField("_isFiring", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponCharacterAnimatorField =
            typeof(FPSWeapon).GetField("characterAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponAnimatorField =
            typeof(FPSWeapon).GetField("weaponAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Pdw90SmoothAmmoWeightField =
            typeof(Pdw90Animation).GetField("_smoothAmmoWeight", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly int IdleHash = Animator.StringToHash("Idle");
        private static readonly int GrappleHash = Animator.StringToHash("Grapple");
        private static readonly int GrappleWeaponIndexHash = Animator.StringToHash("GrappleWeaponIndex");
        private static readonly Vector3 FixedUpperarmLeftPositionOffset = new(0f, 0.027f, 0f);

        private const float RuntimeGrappleClavicleOffsetScale = 1f;
        private const float GrappleOffsetBlendInNormalized = 0.06f;
        private const float GrappleOffsetBlendOutStartNormalized = 0.82f;
        private const float GrappleOffsetBlendOutEndNormalized = 0.98f;

        /// <summary>Animator layer index where the Grapple blend tree runs (left-hand/grapple layer).</summary>
        private const int GrappleLayerIndex = 8;
        private static readonly Vector3 FixedTwistLeftEulerOffset = new(0f, -7.5f, 0f);
        private static readonly int IsInAir = Animator.StringToHash("IsInAir");
        private static readonly Vector3 DefaultAkViewmodelLocalPosition = new(0.1699999f, -1.750005f, 0f);
        private static bool sHasAkViewmodelReference;
        private static Vector3 sAkViewmodelLocalPosition = DefaultAkViewmodelLocalPosition;
        private static bool sHasAkAnchorFrame1CameraReference;
        private static Vector3 sAkAnchorFrame1CameraLocal;
        private static readonly HashSet<int> MissingKinemationSpecialHandlingWarnings = new();
        private static readonly HashSet<int> MissingKinemationGrappleIndexWarnings = new();
        private static readonly HashSet<int> MissingKinemationPartReferenceWarnings = new();
        private static readonly HashSet<int> InvalidKinemationPartReferenceWarnings = new();
        private static readonly HashSet<int> MissingKinemationReloadSoundIndexWarnings = new();
        private const int DrakeTopShellReferenceKey = 11;
        private const int DrakeBottomShellReferenceKey = 12;
        private const int KarLoopBulletReferenceKey = 13;
        private const int FpMuzzleReferenceKey = 21;
    }
}
