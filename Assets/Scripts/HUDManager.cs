using UnityEngine;
using UnityEngine.UIElements;

public class HUDManager : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;

    private ProgressBar _healthBar;
    private Label _healthValue;
    private Label _ammoCurrent;
    private Label _ammoTotal;

    private void OnEnable()
    {
        var root = uiDocument.rootVisualElement;

        _healthBar = root.Q<ProgressBar>("health-bar");
        _healthValue = root.Q<Label>("health-value");
        _ammoCurrent = root.Q<Label>("ammo-current");
        _ammoTotal = root.Q<Label>("ammo-total");
    }

    // Call from Player
    public void UpdateHealth(float current, float max)
    {
        var percent = (current / max) * 100f;
        _healthBar.value = percent;
        _healthValue.text = Mathf.CeilToInt(current).ToString();
    }

    // Call from Weapon
    public void UpdateAmmo(int current, int total)
    {
        _ammoCurrent.text = current.ToString();
        _ammoTotal.text = total.ToString();
    }
}
