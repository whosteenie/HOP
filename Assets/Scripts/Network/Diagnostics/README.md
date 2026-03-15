# Event Bus Debugging System

This system provides comprehensive debugging for silent failures in the game. All failures are published as events through the Event Bus, making them visible in the Event Bus Debug Window.

## Setup

1. **Add DebugEventLogger to your scene:**
   - Create an empty GameObject in your scene
   - Add the `DebugEventLogger` component to it
   - Configure which events you want to log (all enabled by default)

2. **Open the Event Bus Debug Window:**
   - Go to `Tools > Event Bus Debugger` in Unity Editor
   - Enter Play Mode to see events in real-time

## Available Debug Events

### ComponentNotFoundEvent
Published when a component lookup fails (GetComponent, GetComponentInChildren, etc.)

### NetworkObjectReferenceFailedEvent
Published when a NetworkObjectReference.TryGet fails

### SingletonNotAvailableEvent
Published when a required singleton is not available

### CriticalErrorEvent
Published when a critical error occurs (exceptions, etc.)

### NetworkRpcFailedEvent
Published when a network RPC fails

### GameObjectNotFoundEvent
Published when a required GameObject is null or missing

## Helper Methods

Use these helper methods instead of direct Unity API calls to automatically publish debug events:

**Note:** Import the namespace: `using Network.Diagnostics;`

### Component Lookups
```csharp
// Instead of: GetComponent<PlayerController>()
var controller = this.GetComponentSafe<PlayerController>("Context description");

// Instead of: GetComponentInChildren<Renderer>()
var renderer = gameObject.GetComponentInChildrenSafe<Renderer>(includeInactive: false, "Context");
```

### Network Object References
```csharp
// Instead of: if(!targetRef.TryGet(out var networkObject))
if(!DebugHelpers.TryGetNetworkObject(targetRef, out var networkObject, OwnerClientId, "Context")) {
    return;
}
```

### Singleton Access
```csharp
// Instead of: if(SessionManager.Instance != null)
var sessionManager = DebugHelpers.GetSingletonSafe(SessionManager.Instance, "SessionManager", "Context");
if(sessionManager == null) return;
```

### Finding Objects
```csharp
// Instead of: FindFirstObjectByType<LoadoutManager>()
var loadout = DebugHelpers.FindFirstObjectByTypeSafe<LoadoutManager>("Context");
```

### Critical Errors
```csharp
try {
    // Some code
} catch(Exception e) {
    DebugHelpers.PublishCriticalError($"Operation failed: {e.Message}", "Context", e);
    Debug.LogException(e);
}
```

## Benefits

1. **No Silent Failures**: Every failure becomes a visible event
2. **Real-Time Monitoring**: See failures as they happen in the Event Bus Debug Window
3. **Pattern Detection**: Identify recurring issues (e.g., "PlayerController not found" happening frequently)
4. **Full Context**: Every event includes context about where and why it failed
5. **Centralized Logging**: All debug events go through the Event Bus, making them easy to track

## Migration Guide

When you encounter silent failures in your code:

1. Replace direct Unity API calls with helper methods:
   - `GetComponent<T>()` → `GetComponentSafe<T>("context")`
   - `FindFirstObjectByType<T>()` → `FindFirstObjectByTypeSafe<T>("context")`
   - `TryGet()` → `TryGetNetworkObject()`

2. Wrap exception handlers with `PublishCriticalError()`:
   ```csharp
   catch(Exception e) {
       DebugHelpers.PublishCriticalError($"Error message", "Context", e);
   }
   ```

3. Check singleton access with `GetSingletonSafe()`:
   ```csharp
   var manager = DebugHelpers.GetSingletonSafe(Manager.Instance, "Manager", "Context");
   ```

## Example Usage

```csharp
using Network.Diagnostics;

public class MyComponent : MonoBehaviour {
    private PlayerController playerController;
    
    private void Awake() {
        // This will publish ComponentNotFoundEvent if PlayerController is not found
        playerController = this.GetComponentSafe<PlayerController>("MyComponent.Awake");
        if(playerController == null) {
            enabled = false;
            return;
        }
    }
    
    private void OnNetworkObjectReference(NetworkObjectReference ref) {
        // This will publish NetworkObjectReferenceFailedEvent if TryGet fails
        if(!DebugHelpers.TryGetNetworkObject(ref, out var networkObject, OwnerClientId, 
               "MyComponent.OnNetworkObjectReference")) {
            return;
        }
    }
}
```

