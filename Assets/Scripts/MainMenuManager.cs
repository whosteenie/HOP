using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

public class MainMenuManager : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private string gameSceneName = "Game";

    private void OnEnable()
    {
        var root = uiDocument.rootVisualElement;

        root.Q<Button>("play-button").clicked += () => SceneManager.LoadScene(gameSceneName);
        root.Q<Button>("options-button").clicked += OpenOptions;
        root.Q<Button>("credits-button").clicked += OpenCredits;
        root.Q<Button>("quit-button").clicked += Application.Quit;
    }

    private void OpenOptions()
    {
        Debug.Log("Open Options (link to PauseMenu or separate panel)");
        // Or: gameObject.SetActive(false); OptionsPanel.SetActive(true);
    }

    private void OpenCredits()
    {
        Debug.Log("Open Credits");
    }
}
