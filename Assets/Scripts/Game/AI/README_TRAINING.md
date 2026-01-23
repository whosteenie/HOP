# ML-Agents Bot Training Guide

## Overview

This guide explains how to record demonstrations and train AI bots for HOP using GAIL (Generative Adversarial Imitation Learning).

## Setup Complete ✓

- Python ML-Agents installed
- Unity ML-Agents package installed
- BotAgent.cs created
- PlayerInput.cs refactored for bot control

## Phase 4: Recording Demonstrations

### Prerequisites

1. Create a bot player prefab variant (see "Creating Bot Prefab" below)
2. Place stationary target players in scene (optional for movement-only training)
3. Ensure `Assets/Demonstrations/` folder exists

### Creating Bot Prefab

1. Duplicate `Assets/PlayerAssets/Player.prefab` → rename to `Player_Bot.prefab`
2. Add components to the root GameObject:
   - `BotAgent` script
   - `Behavior Parameters` component:
     - Behavior Name: "HopMovement"
     - Vector Observation Space Size: 30
     - Actions:
       - Continuous: 4
       - Discrete Branches: 2 (size 2, 2)
     - Behavior Type: "Heuristic Only" (for recording)
     - Model: None (will assign after training)
   - `Decision Requester` component: **REQUIRED**
     - Decision Period: 5 (agent steps every 5 frames)
     - Take Actions Between Decisions: Checked
   - `Demonstration Recorder` component:
     - Record: false (toggle via F9 or inspector)
     - Demonstration Name: "HOP_Movement_v1"
     - Demonstration Directory: "Assets/Demonstrations"
   - `DemoRecordingHelper` script

### Recording Session

1. **Delete any old demo files** from `Assets/Demonstrations/` (if starting fresh)
2. **Spawn as bot prefab** in a normal match (host a game, spawn as Player_Bot)
3. **Press F9** to start recording (you'll see "● RECORDING" in top-right)
4. **Console should show:**
   - `[DemoRecordingHelper] Recording ENABLED`
   - `[DemoRecordingHelper] Recording STARTED in Game scene`
5. **Play naturally** for 30-60 minutes:
   - Grapple around the map
   - Use air strafing to gain/maintain speed
   - Use jump pads
   - Chain grapples (grapple → air strafe → grapple)
   - Vary your routes and patterns
6. **Press F9** to stop recording
7. **Verify** `Assets/Demonstrations/HOP_Movement_v1.demo`:
   - File exists
   - File size is **20-50MB+** (10 min = ~20-25MB, 30 min = ~60-75MB)
   - If file is only 1KB, recording failed - check troubleshooting below

### Recording Best Practices

**DO:**
- Vary movement patterns (different routes, different speeds)
- Show successful grapples AND failed grapples
- Use all movement mechanics (walk, sprint, crouch, jump, grapple)
- Chain mechanics together (grapple → jump pad → air strafe)
- Record smooth, intentional movement
- Stay in Game scene (recording auto-pauses in menus)

**DON'T:**
- Stand still for long periods
- Repeat the same route over and over
- Record while in menus/lobby (DemoRecordingHelper prevents this)
- Have the recorder on stationary target players (they'll learn to stand still)

### Recommended Recording Sessions

1. **Session 1 (30 min):** Basic grappling
   - Grapple to different points
   - Short and long grapples
   - Horizontal and vertical grapples

2. **Session 2 (30 min):** Air strafing
   - Gain speed with air strafe
   - Maintain momentum between grapples
   - Use air strafe to navigate tight spaces

3. **Session 3 (30 min):** Jump pads and combinations
   - Use jump pads when available
   - Combine jump pad → grapple → air strafe
   - Use mega pads for height

4. **Session 4 (30 min):** Advanced chains
   - Long grapple chains (3-4+ in a row)
   - Complex routes through the map
   - High-speed movement

## Phase 5: Training the Model

### Prerequisites

- At least 30 minutes of demonstration data in `Assets/Demonstrations/HOP_Movement_v1.demo`
- Python environment with mlagents installed

### Training Command

1. **Activate Python environment:**
   ```bash
   # Windows
   mlagents-env\Scripts\activate
   
   # Mac/Linux
   source mlagents-env/bin/activate
   ```

2. **Start training:**
   ```bash
   cd "C:\Users\justi\Documents\Unity Projects\HOP"
   mlagents-learn config/hop_movement_gail.yaml --run-id=HopMovement_v1 --force
   ```

3. **In Unity:** Press Play when prompted

4. **Monitor training:**
   - Open another terminal
   - Run: `tensorboard --logdir results`
   - Open browser: http://localhost:6006

### Training Metrics to Watch

- **Policy/Extrinsic Reward:** Should increase (more positive)
- **Losses/Policy Loss:** Should decrease and stabilize
- **GAIL/Discriminator Loss:** Should hover around 0.5-0.7 (balanced)
- **Environment/Episode Length:** Should stabilize
- **Environment/Cumulative Reward:** Should increase

### Training Duration

- **Quick test:** 500K steps (~1-2 hours)
- **Basic competence:** 2M steps (~6-8 hours)
- **Good performance:** 5M steps (~12-24 hours)

Stop training when metrics plateau (usually around 3-5M steps).

### Stopping Training

- Press Ctrl+C in the training terminal
- Model checkpoints are saved in `results/HopMovement_v1/`
- Find the `.onnx` file (e.g., `HopMovement_v1.onnx`)

## Phase 6: Deploying the Trained Model

### Export Model

1. Find trained model: `results/HopMovement_v1/HopMovement_v1.onnx`
2. Copy to Unity: `Assets/ML-Models/HopMovement_v1.onnx` (create folder if needed)
3. Wait for Unity to import (shows as "NNModel" asset)

### Assign Model to Bot Prefab

1. Open `Player_Bot.prefab`
2. Select root GameObject
3. Find `Behavior Parameters` component
4. Set "Behavior Type" to "Inference Only"
5. Drag `HopMovement_v1.onnx` into "Model" field
6. Save prefab

### Testing

1. Host a match
2. Spawn bot using the bot prefab (manual or via spawn system)
3. Observe bot behavior:
   - Does it move naturally?
   - Does it use grapple?
   - Does it use air strafing?
   - Does it navigate the map without falling?

## Troubleshooting

### Demo file is only 1KB (recording failed)
**Problem:** Recording didn't capture any actions (0 steps recorded).

**Most Common Cause:** Missing `Decision Requester` component!

**Solutions:**
1. **Add `Decision Requester` component** to Player_Bot prefab (most likely fix)
   - Set Decision Period: 5
   - Enable "Take Actions Between Decisions"
2. Delete the corrupted `.demo` file
3. Verify `Behavior Type` is "Heuristic Only" (not "Inference Only")
4. Check `BotAgent` initialization logs in console:
   - Should show: `BehaviorType=HeuristicOnly, IsBot=False`
   - If it shows `IsBot=True`, your input is blocked
5. Ensure you're moving around - demo records your actual gameplay
6. Check `Num Steps To Record` is set to `0` or `-1` (unlimited)
7. Try recording again for at least 2-3 minutes to test

**Expected Results (2 min recording):**
- File size: 5-10MB (not 1KB)
- Num Steps: 1000-2000+
- Mean Reward: Small number (not 0)

### Bot doesn't move
- Check "Behavior Type" is "Inference Only", not "Default" or "Heuristic Only"
- Verify `BotAgent.IsBot = true` is set
- Check console for errors

### Bot movement is jerky/unnatural
- Record smoother demonstrations
- Increase training steps
- Check if framerate is stable during recording (60 FPS recommended)

### Training crashes
- Reduce `hidden_units` to 256 in config
- Reduce `buffer_size` to 5120
- Check Python/TensorFlow compatibility

### Bot falls off map repeatedly
- Record more navigation demonstrations
- Show falling AND recovering in demos
- Add negative reward for falling (if using RL hybrid)

### "No demonstrations found" error
- Verify path: `Assets/Demonstrations/HOP_Movement_v1.demo` exists
- Check file size (should be > 1MB for 30 min recording)
- Ensure recording was active (check for "● RECORDING" indicator)

### Input doesn't work during recording
- Verify `Behavior Type` is "Heuristic Only"
- Check console for `[BotAgent] Initialize` log - should show `IsBot=False`
- If `IsBot=True`, the `BotAgent` is incorrectly blocking your input

## Iteration Loop

1. Test deployed model
2. Identify weak areas (e.g., bot doesn't chain grapples)
3. Record 30 min of demos focusing on that behavior
4. Retrain with combined demos (old + new)
5. Deploy and test again
6. Repeat until acceptable performance

## Advanced: Hybrid RL + Imitation

Once movement works, you can add reinforcement learning rewards:

1. Edit `config/hop_movement_gail.yaml`:
   - Change `gail.strength` from 1.0 to 0.5
   - Change `extrinsic.strength` from 0.1 to 1.0

2. Add rewards in `BotAgent.OnActionReceived()`:
   ```csharp
   // Reward for gaining height
   if(_transform.position.y > _lastHeight) {
       AddReward(0.01f);
   }
   
   // Reward for speed
   if(_movement.HorizontalVelocity.magnitude > 15f) {
       AddReward(0.005f);
   }
   ```

3. Retrain - bot will use your demos as a base but learn to optimize beyond them

## Next Steps: Adding Combat

After movement is working well:

1. Record combat demos (shooting stationary targets)
2. Update observations to include:
   - Enemy in crosshair (raycast)
   - Weapon ammo
   - Damage multiplier
3. Add discrete action: Shoot (0/1)
4. Add rewards: +1 for damage dealt, +10 for kills, -5 for deaths
5. Retrain with new demos

This creates a bot that moves well (from imitation) and learns to aim/shoot (from RL).

