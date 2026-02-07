## Vivox production setup (UGS Cloud Code token minting)

This project is wired to use **server-side Vivox Access Tokens (VATs)** via UGS Cloud Code.

Client code:
- `Assets/Scripts/Game/Social/VivoxCloudCodeTokenProvider.cs` (implements `IVivoxTokenProvider`)
- `Assets/Scripts/Game/Social/VoiceManager.cs` registers the provider before `VivoxService.Instance.InitializeAsync()`

Cloud Code:
- `CloudCode/VivoxToken.js` (script you deploy to UGS Cloud Code)

### What this enables

- You can **disable Vivox Test Mode**
- You can **remove the Vivox Token Key from the Unity project settings**
- Builds won’t ship the signing key

### UGS dashboard setup checklist

In the Unity Dashboard (UGS):

- **Enable Authentication**
  - Enable **Anonymous** sign-in (silent).
- **Enable Cloud Code**
  - Create a Cloud Code **Script** named **`VivoxToken`**
  - Paste the contents of `CloudCode/VivoxToken.js` into it.
  - Configure **Secret Manager** secrets (per environment):
    - `VIVOX_ISSUER`: your Vivox token issuer (title id)
    - `VIVOX_TOKEN_KEY`: your Vivox signing key (secret)

### Unity Editor project settings checklist

- `Edit > Project Settings > Services`:
  - Ensure your project is linked to the correct UGS project/environment.
- `Edit > Project Settings > Services > Vivox`:
  - **Disable Test Mode**
  - Ensure the **Token Key is empty/cleared** (the build validator blocks shipping if it isn’t)

### Build safety

There is a build-time validator that blocks non-development builds if Vivox is still configured to generate local tokens:
- `Assets/Scripts/Editor/VivoxBuildValidator.cs`

If you hit a build error from it, fix the Vivox settings above (disable Test Mode + clear token key).

