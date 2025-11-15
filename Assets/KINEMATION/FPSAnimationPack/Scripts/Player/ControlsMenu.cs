// Designed by KINEMATION, 2025.

using TMPro;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Player
{
    public class ControlsMenu : MonoBehaviour
    {
        [SerializeField] private GameObject controlsMenu;

        [SerializeField] private TMP_Text weaponText;
        [SerializeField] private TMP_Text ammoLeftText;
        [SerializeField] private TMP_Text ammoTotalText;
        [SerializeField] private TMP_Text fireModeText;
        [SerializeField] private TMP_Text activeAnimationText;

        private Animator _animator;
        private bool _areControlsToggled;
        private FPSPlayer _player;
        
        private void Start()
        {
            _player = FindObjectsByType<FPSPlayer>(FindObjectsSortMode.None)[0];
            _animator = _player.GetComponent<Animator>();
            controlsMenu.SetActive(false);
        }

        private bool TryGetActiveAnimName(int layerIndex)
        {
            var stateInfo = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            if (stateInfo.normalizedTime >= 1f) return false;
            
            var clipInfos = _animator.GetCurrentAnimatorClipInfo(layerIndex);
            if (clipInfos.Length == 0) return false;
            
            activeAnimationText.SetText(clipInfos[0].clip.name);
            return true;
        }
        
        private void UpdateActiveAnimationName()
        {
            if (TryGetActiveAnimName(4)) return;
            if (TryGetActiveAnimName(2)) return;
            if (TryGetActiveAnimName(1)) return;
            
            activeAnimationText.SetText("None");
        }
        
        private void Update()
        {
            weaponText.SetText(_player.GetActivePrefab().name);
            ammoLeftText.SetText(_player.GetActiveWeapon().GetActiveAmmo().ToString());
            ammoTotalText.SetText(_player.GetActiveWeapon().weaponSettings.ammo.ToString());
            fireModeText.SetText(_player.GetActiveWeapon().ActiveFireMode.ToString());
            
            UpdateActiveAnimationName();

            if (Input.GetKeyDown(KeyCode.Tab))
            {
                _areControlsToggled = !_areControlsToggled;
                controlsMenu.SetActive(_areControlsToggled);
            }

            if (!Application.isEditor && Input.GetKeyDown(KeyCode.Escape)) Application.Quit(0);
        }
    }
}
