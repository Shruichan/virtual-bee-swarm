using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>BeeswaxBody's first step of the frame: takes last frame's visual offset back off before anything moves him.</summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class BeeswaxBodyEarly : MonoBehaviour
    {
        BeeswaxBody _body;

        void Awake() => _body = GetComponent<BeeswaxBody>();

        void Update()
        {
            if (_body != null && _body.enabled) _body.UndoVisual();
        }
    }
}
