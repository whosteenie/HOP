# Hopball Indicator Setup Guide

This guide explains how to set up the Hopball World Indicator prefab for the hopball gamemode.

## Overview

The Hopball Indicator is a 3D world-space UI element that shows above the hopball holder's head (or the dropped hopball). It displays team-based colors, distance, and handles off-screen clamping with a directional arrow.

## Prefab Structure

Create a prefab with the following hierarchy:

```
HopballIndicator (GameObject)
├─ HopballIndicator (Script Component)
└─ Canvas (Canvas Component - World Space)
    ├─ IndicatorContainer (RectTransform)
    │   ├─ CircleIndicator (Image) - Circle sprite for equipped state
    │   ├─ DiamondIndicator (Image) - Diamond sprite for dropped state
    │   ├─ IconContainer (RectTransform)
    │   │   ├─ IconImage (Image) - Optional logo sprite
    │   │   └─ IconText (TextMeshProUGUI) - "HB" text fallback
    │   └─ DistanceText (TextMeshProUGUI) - Distance display
    └─ ArrowContainer (RectTransform) - Only visible when off-screen
        └─ ArrowImage (Image) - Directional arrow sprite
```

**Note**: ArrowContainer is a child of Canvas but will be positioned independently in world space when off-screen.

## Canvas Settings

1. **Canvas Component**:
   - Render Mode: **World Space**
   - Pixel Perfect: **Unchecked**
   - Sort Order: **100** (or higher to render above other UI)

2. **Canvas Scaler** (Optional but recommended):
   - UI Scale Mode: **Constant Pixel Size**
   - Scale Factor: **1**

3. **Graphic Raycaster**: Can be disabled (not needed for this UI)

## Component Setup

### HopballIndicator Script

Assign the following references in the inspector:

- **Canvas**: The Canvas component on this GameObject
- **Circle Indicator**: The Image component for the circle (equipped state)
- **Diamond Indicator**: The Image component for the diamond (dropped state)
- **Icon Image**: The Image component for the logo sprite (optional)
- **Icon Text**: The TextMeshProUGUI component for "HB" fallback text
- **Distance Text**: The TextMeshProUGUI component for distance display
- **Arrow Container**: The RectTransform of the arrow container
- **Arrow Image**: The Image component for the arrow sprite

### Settings

- **Height Offset**: 2.5 (height above player head or hopball)
- **Screen Edge Margin**: 50 (margin from screen edge when off-screen)
- **Arrow Distance**: 30 (distance between indicator and arrow when off-screen)
- **Logo Sprite**: (Optional) Assign your custom logo sprite here

### Colors

- **Team Color**: #6496FF (Blue - for teammates)
- **Enemy Color**: #FF6464 (Red - for enemies)
- **Dropped Color**: #8C18EE (Purple - for dropped hopball)

## Image Components Setup

### Circle Indicator (Equipped State)

- **Image Type**: Simple
- **Color**: Will be set by script (team/enemy color)
- **Sprite**: Create or assign a circle sprite (white/transparent center recommended)
- **Preserve Aspect**: Checked

### Diamond Indicator (Dropped State)

- **Image Type**: Simple
- **Color**: Will be set by script (purple)
- **Sprite**: Create or assign a diamond sprite (white/transparent center recommended)
- **Preserve Aspect**: Checked

### Icon Image (Optional Logo)

- **Image Type**: Simple
- **Color**: White
- **Sprite**: Your custom logo sprite (leave empty to use "HB" text fallback)
- **Preserve Aspect**: Checked

### Icon Text (Fallback)

- **Text**: "HB"
- **Font Size**: 24-32 (adjust based on circle size)
- **Alignment**: Center (both horizontal and vertical)
- **Color**: White
- **Font Style**: Bold

### Distance Text

- **Text**: "0m" (will be updated by script)
- **Font Size**: 16-20
- **Alignment**: Center (horizontal), Bottom (vertical)
- **Color**: White
- **Outline**: Add Outline component with black color and 2-3 width

### Arrow Image

- **Image Type**: Simple
- **Color**: White (or match indicator color)
- **Sprite**: Create or assign an arrow sprite pointing up
- **Preserve Aspect**: Checked

## RectTransform Sizes

Recommended sizes (adjust as needed):

- **Canvas**: Scale (0.001, 0.001, 0.001) - Makes canvas small in world space
- **IndicatorContainer**: Width: 100, Height: 100
- **Circle/Diamond Indicators**: Width: 80, Height: 80
- **IconContainer**: Width: 60, Height: 60
- **IconImage/IconText**: Width: 50, Height: 50
- **DistanceText**: Width: 100, Height: 30
- **ArrowContainer**: Width: 40, Height: 40
- **ArrowImage**: Width: 30, Height: 30

## Manager Setup

1. Add **HopballIndicatorManager** component to a GameObject in the Game scene (or wherever HopballSpawnManager is)
2. Assign the **HopballIndicator** prefab to the "Indicator Prefab" field
3. The manager will automatically create/destroy indicators as needed

## Testing

1. Enter Hopball gamemode
2. Pick up the hopball - should see blue/red circle above your head
3. Drop the hopball - should see purple diamond above the ball
4. Move off-screen - arrow should appear pointing toward the indicator
5. Test with teammates (blue) and enemies (red)

## Notes

- The indicator is instantiated per-client, so each player sees their own version facing their camera
- The indicator automatically handles team color based on the holder's team vs your team
- Distance is calculated from your camera to the target position
- Off-screen clamping works from all directions (top, bottom, left, right, behind camera)

