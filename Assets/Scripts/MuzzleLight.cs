using System.Collections;
using UnityEngine;

public class MuzzleLight : MonoBehaviour
{
    public float lightDuration = 0.05f;
    
    private void OnEnable()
    {
        StartCoroutine(DisableAfterDelay());
    }

    private IEnumerator DisableAfterDelay()
    {
        yield return new WaitForSeconds(lightDuration);
        gameObject.SetActive(false);
    }
}
