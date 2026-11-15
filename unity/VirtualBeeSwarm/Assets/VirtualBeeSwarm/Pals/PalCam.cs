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
            float spread = 0f;
            var honey = HoneyFrame.Instance;
            if (honey != null && honey.Active) { focus = Vector3.Lerp(honey.Position, honey.JarTop, 0.4f); spread = 0.35f; }
            else if (hands != null && hands.GhostUp)
            {
                var g = hands.Hands[1];
                if (g.normal.y > 0.7f) { focus = g.centre; spread = 0.12f; } // palm up: the bees on it
                else { focus = Vector3.Lerp(b, g.centre, 0.5f); spread = Mathf.Min(0.5f, Vector3.Distance(b, g.centre) * 0.5f); }
            }
            Vector3 eye = head.position;
            Vector3 to = focus - eye;
            to.y = 0f;
            Vector3 fwd = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float span = spread > 0f ? Mathf.Max(0.25f, spread * 2f) : Mathf.Clamp(PalBeeswax.Radius() * 3f, 0.35f, 1.2f);
            Vector3 pos = focus - fwd * (span * 1.6f) + right * (span * 0.45f) + Vector3.up * (span * 0.25f);
            _look = _init ? Vector3.Lerp(_look, focus, 1f - Mathf.Exp(-Time.deltaTime * 6f)) : focus;
            _init = true;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(_look - pos, Vector3.up));
        }
    }
}
