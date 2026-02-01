using UnityEngine;
using UnityEngine.UI;
using Discord.Sdk;
using System.Linq;

public class DiscordManager : MonoBehaviour
{
    [SerializeField] 
    private ulong clientId; // Set this in the Unity Inspector from the dev portal
    
    [SerializeField]
    private Button loginButton; 
    
    [SerializeField] 
    private Text statusText;

    private Client client;
    private string codeVerifier;
}