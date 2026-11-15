using UnityEngine;

namespace VirtualBeeSwarm.Pals
{
    /// <summary>
    /// Capture helper (Capture/ShowCapture view "pal"): a camera beside the player's head that keeps Beeswax and the
    /// ghost hand in frame, so stills show the hand moves up close. Not used in the game.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class PalCam : MonoBehaviour
    {
        BeeswaxController _bw;
        Vector3 _look;
        bool _init;

        void LateUpdate()
        {
            if (_bw == null) _bw = FindAnyObjectByType<BeeswaxController>();
            var head = Camera.main != null ? Camera.main.transform : null;
            if (_bw == null || head == null) return;
            Vector3 b = _bw.BodyTransform != null ? _bw.BodyTransform.position : _bw.transform.position;
            var hands = PalHands.Instance;
            Vector3 focus = b;
            if (hands != null && hands.GhostUp) focus = Vector3.Lerp(b, hands.Hands[1].centre, 0.5f);
            Vector3 eye = head.position;
            Vector3 to = focus - eye;
            to.y = 0f;
            Vector3 fwd = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float span = Mathf.Max(0.35f, Vector3.Distance(b, focus) * 2f + PalBeeswax.Radius() * 2.5f);
            Vector3 pos = focus - fwd * (span * 1.6f) + right * (span * 0.45f) + Vector3.up * (span * 0.25f);
            _look = _init ? Vector3.Lerp(_look, focus, 1f - Mathf.Exp(-Time.deltaTime * 6f)) : focus;
            _init = true;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(_look - pos, Vector3.up));
        }
    }
}
