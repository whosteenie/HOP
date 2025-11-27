using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

public class LoadingBallAnimation : MonoBehaviour {
    private VisualElement _ball;
    private Coroutine _animationCoroutine;
    private bool _isAnimating;

    private const float AnimationDuration = 1.0f;
    private const float HopHeight = 40f;
    private const float MaxSquashX = 1.3f;
    private const float MaxSquashY = 0.6f;
    private const float SquashHoldDuration = 0.1f; // Time to hold max squash (as fraction of total duration)
    private const float SquashStartT = 0.85f; // When squash begins (ball hits ground)
    private const float MaxSquashT = 0.95f; // When max squash is reached
    private const float SquashEndT = 1.0f; // When unsquash completes and ball starts rising

    public void StartAnimation(VisualElement ball) {
        if(ball == null) {
            Debug.LogWarning("[LoadingBallAnimation] Ball VisualElement == null!");
            return;
        }

        _ball = ball;

        if(_animationCoroutine != null) {
            StopCoroutine(_animationCoroutine);
        }

        _isAnimating = true;
        _animationCoroutine = StartCoroutine(AnimateBall());
    }

    public void StopAnimation() {
        _isAnimating = false;
        if(_animationCoroutine != null) {
            StopCoroutine(_animationCoroutine);
            _animationCoroutine = null;
        }

        // Reset ball to default state
        if(_ball == null) return;
        _ball.style.translate = new StyleTranslate(new Translate(0, 0));
        _ball.style.scale = new StyleScale(new Scale(Vector2.one));
    }

    private IEnumerator AnimateBall() {
        while(_isAnimating && _ball != null) {
            var elapsed = 0f;

            while(elapsed < AnimationDuration && _isAnimating && _ball != null) {
                elapsed += Time.deltaTime;
                var t = elapsed / AnimationDuration;

                // Calculate position (gravity effect)
                var y = CalculateYPosition(t);

                // Calculate scale (squash on impact)
                var scale = CalculateScale(t);

                // Apply transforms
                if(_ball != null) {
                    _ball.style.translate = new StyleTranslate(new Translate(0, -y));
                    _ball.style.scale = new StyleScale(new Scale(scale));
                }

                yield return null;
            }
        }
    }

    private static float CalculateYPosition(float t) {
        // Ball only moves when unsquashed. When squashing, it's on the ground (y=0).
        // Rising -> Apex -> Falling -> Ground (squash) -> Ground (unsquash) -> repeat

        if(t >= SquashStartT) {
            // Ball is on the ground during squash/unsquash phases
            return 0f;
        }

        // Ball is in the air - calculate bounce
        // Remap t from [0, SquashStartT] to [0, 1] for the air phase
        var airT = t / SquashStartT;
        const float apexT = 0.5f; // Apex is at 50% of air phase

        if(airT <= apexT) {
            // Going up: smooth acceleration then deceleration
            var localT = airT / apexT;
            return Mathf.Lerp(0f, HopHeight, EaseOutQuad(localT));
        } else {
            // Coming down: smooth acceleration
            var localT = (airT - apexT) / (1f - apexT);
            return Mathf.Lerp(HopHeight, 0f, EaseInQuad(localT));
        }
    }

    private static Vector2 CalculateScale(float t) {
        // Ball is on the ground during squash phases, so scale based on time
        // 1. Rising (t: 0 -> ~0.4): Normal scale
        // 2. Apex (t: ~0.4): Normal scale
        // 3. Falling (t: ~0.4 -> squash start): Normal scale
        // 4. Squash (t: squash start -> max squash): Ease into squash
        // 5. Max squash (t: max squash -> hold end): Hold max squash
        // 6. Unsquash (t: hold end -> 1.0): Ease out of squash
        // 7. Repeat

        const float squashHoldEndT = MaxSquashT + SquashHoldDuration;

        switch(t) {
            case >= SquashStartT and < MaxSquashT: {
                // Phase 4: Squash - ease into max squash as ball hits ground
                var localT = (t - SquashStartT) / (MaxSquashT - SquashStartT);
                var easedIntensity = EaseInQuad(localT);
                var squashX = Mathf.Lerp(1f, MaxSquashX, easedIntensity);
                var squashY = Mathf.Lerp(1f, MaxSquashY, easedIntensity);
                return new Vector2(squashX, squashY);
            }
            case >= MaxSquashT and < squashHoldEndT:
                // Phase 5: Max squash - hold at maximum squash
                return new Vector2(MaxSquashX, MaxSquashY);
            default: {
                if(t >= squashHoldEndT && t < SquashEndT) {
                    // Phase 6: Unsquash - ease out of squash while on ground
                    var localT = (t - squashHoldEndT) / (SquashEndT - squashHoldEndT);
                    var easedIntensity = EaseOutQuad(1f - localT);
                    var squashX = Mathf.Lerp(1f, MaxSquashX, easedIntensity);
                    var squashY = Mathf.Lerp(1f, MaxSquashY, easedIntensity);
                    return new Vector2(squashX, squashY);
                } else {
                    // Phases 1-3: Rising, apex, falling - normal scale
                    return Vector2.one;
                }
            }
        }
    }

    private static float EaseOutQuad(float t) {
        return 1f - (1f - t) * (1f - t);
    }

    private static float EaseInQuad(float t) {
        return t * t;
    }
}