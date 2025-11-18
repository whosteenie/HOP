using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class LoadingBallAnimation : MonoBehaviour {
        private VisualElement _ball;
        private Coroutine _animationCoroutine;
        private bool _isAnimating;
        
        private const float AnimationDuration = 0.9f;
        private const float HopHeight = 40f;
        private const float MaxSquashX = 1.25f;
        private const float MaxSquashY = 0.65f;
        
        public void StartAnimation(VisualElement ball) {
            if(ball == null) {
                Debug.LogWarning("[LoadingBallAnimation] Ball VisualElement is null!");
                return;
            }
            
            _ball = ball;
            
            if(_animationCoroutine != null) {
                StopCoroutine(_animationCoroutine);
            }
            
            _isAnimating = true;
            _animationCoroutine = StartCoroutine(AnimateBall());
            Debug.Log("[LoadingBallAnimation] Started animation coroutine");
        }
        
        public void StopAnimation() {
            _isAnimating = false;
            if(_animationCoroutine != null) {
                StopCoroutine(_animationCoroutine);
                _animationCoroutine = null;
            }
            
            // Reset ball to default state
            if(_ball != null) {
                _ball.style.translate = new StyleTranslate(new Translate(0, 0));
                _ball.style.scale = new StyleScale(new Scale(Vector2.one));
            }
        }
        
        private IEnumerator AnimateBall() {
            Debug.Log("[LoadingBallAnimation] Animation coroutine started");
            int loopCount = 0;
            
            while(_isAnimating && _ball != null) {
                float elapsed = 0f;
                
                while(elapsed < AnimationDuration && _isAnimating && _ball != null) {
                    elapsed += Time.deltaTime;
                    float t = elapsed / AnimationDuration;
                    
                    // Calculate position (gravity effect)
                    float y = CalculateYPosition(t);
                    
                    // Calculate scale (squash on impact)
                    Vector2 scale = CalculateScale(t, y);
                    
                    // Apply transforms
                    if(_ball != null) {
                        _ball.style.translate = new StyleTranslate(new Translate(0, -y));
                        _ball.style.scale = new StyleScale(new Scale(scale));
                    }
                    
                    yield return null;
                }
                
                loopCount++;
                if(loopCount == 1) {
                    Debug.Log($"[LoadingBallAnimation] Completed first animation loop. Ball position: y={CalculateYPosition(0)}, scale={CalculateScale(0, 0)}");
                }
                
                // Reset for next loop iteration
                elapsed = 0f;
            }
            
            Debug.Log("[LoadingBallAnimation] Animation coroutine ended");
        }
        
        private float CalculateYPosition(float t) {
            // Gravity-based hop animation
            // Quick acceleration upward (0-0.2)
            if(t < 0.2f) {
                float localT = t / 0.2f;
                return Mathf.Lerp(0f, 20f, EaseOutQuad(localT));
            }
            // Slowing down approaching apex (0.2-0.48)
            else if(t < 0.48f) {
                float localT = (t - 0.2f) / 0.28f;
                return Mathf.Lerp(20f, HopHeight, EaseInQuad(localT));
            }
            // Apex: floating moment (0.48-0.52)
            else if(t < 0.52f) {
                return HopHeight;
            }
            // Starting to fall (0.52-0.55)
            else if(t < 0.55f) {
                float localT = (t - 0.52f) / 0.03f;
                return Mathf.Lerp(HopHeight, 38f, EaseOutQuad(localT));
            }
            // Accelerating downward (0.55-0.9)
            else if(t < 0.9f) {
                float localT = (t - 0.55f) / 0.35f;
                return Mathf.Lerp(38f, 2f, EaseInQuad(localT));
            }
            // About to hit ground (0.9-0.95)
            else if(t < 0.95f) {
                float localT = (t - 0.9f) / 0.05f;
                return Mathf.Lerp(2f, 0f, EaseInQuad(localT));
            }
            // Impact and bounce back (0.95-1.0)
            else {
                return 0f;
            }
        }
        
        private Vector2 CalculateScale(float t, float y) {
            // Squash when near ground (y < 5px)
            if(y < 5f) {
                if(t >= 0.9f && t < 0.95f) {
                    // Anticipation squash
                    float localT = (t - 0.9f) / 0.05f;
                    float squashX = Mathf.Lerp(1.1f, MaxSquashX, localT);
                    float squashY = Mathf.Lerp(0.85f, MaxSquashY, localT);
                    return new Vector2(squashX, squashY);
                }
                else if(t >= 0.95f && t < 1.0f) {
                    // Impact squash, then bounce back
                    float localT = (t - 0.95f) / 0.05f;
                    float squashX = Mathf.Lerp(MaxSquashX, 1f, EaseOutQuad(localT));
                    float squashY = Mathf.Lerp(MaxSquashY, 1f, EaseOutQuad(localT));
                    return new Vector2(squashX, squashY);
                }
            }
            
            // Slight stretch when falling fast (between 0.7-0.9)
            if(t >= 0.7f && t < 0.9f && y > 5f) {
                float localT = (t - 0.7f) / 0.2f;
                float stretchX = Mathf.Lerp(1.05f, 1.1f, localT);
                float stretchY = Mathf.Lerp(0.95f, 0.85f, localT);
                return new Vector2(stretchX, stretchY);
            }
            
            // Normal scale
            return Vector2.one;
        }
        
        private float EaseOutQuad(float t) {
            return 1f - (1f - t) * (1f - t);
        }
        
        private float EaseInQuad(float t) {
            return t * t;
        }
    }
}
