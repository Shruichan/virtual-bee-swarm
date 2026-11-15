using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// BeeswaxBody's bumps: when something new touches him (his flight grazes the hive, a prop flies into him, a hand pokes
    /// him), he squashes along the hit, wobbles and is knocked back a little; the separation spring brings him back.
    /// How hard comes from his speed plus the thing's, against his size. Half as much (and no knock-back) in comfort mode.
    /// </summary>
    public partial class BeeswaxBody
    {
        float _bumpAt = -10f, _bumpA;
        Vector3 _bumpN;

        partial void Bump(int group, Vector3 n, Vector3 soft, Vector3 vRoot)
        {
            // a new touch: this thing was not touching him for a second (his hover bob against a mark is not a bump)
            if (group < 0 || soft.magnitude < 0.25f * _margin) return;
            float last = _touchAt[group];
            _touchAt[group] = Time.time;
            if (Time.time - last < 1f || group == (int)Group.Head || group == (int)Group.Surface || Time.time - _bumpAt < 0.4f) return;
            // how hard: his own speed plus how fast the thing came at him, against his size
            float vObj = 0f;
            for (int i = 0; i < _parts.Count; i++) if (_parts[i].on && _parts[i].group == group) vObj = Mathf.Max(vObj, Mathf.Max(0f, -Vector3.Dot(_parts[i].vel, n)));
            float a = Mathf.Clamp(0.35f + (Mathf.Max(0f, -Vector3.Dot(vRoot, n)) + vObj) / (4f * _size), 0.35f, 1f);
            bool comfort = VirtualBeeSwarm.Stakes.StakesFlags.Comfort; // comfort mode: half the squash and wobble, no knock-back
            if (comfort) a *= 0.5f;
            _bumpAt = Time.time;
            _bumpA = a;
            _bumpN = n;
            if (!comfort) _sepV += n * (a * 2.5f * _size); // a tiny knock-back, the spring brings him back
            _bumps++;
            if (_bumps <= 40) Debug.Log("[VBS][Body] bump: " + GroupName(group) + " (" + F(a) + ") [beat " + BeatName + "] t=" + F(Time.time));
        }

        partial void ApplyBump()
        {
            float t = Time.time - _bumpAt;
            if (t > 0.9f || _bodyT == null) return;
            float a = _bumpA;
            float sq = a * 0.2f * Mathf.Exp(-t / 0.09f) * Mathf.Cos(2f * Mathf.PI * 3f * t);
            float wob = a * 16f * Mathf.Exp(-t / 0.28f) * Mathf.Sin(2f * Mathf.PI * 4.5f * t);
            _bodyScaleWas = _bodyT.localScale;
            _bodyRotWas = _bodyT.localRotation;
            // squash along the hit, bulge across it (volume kept, roughly)
            Vector3 nl = _bodyT.InverseTransformDirection(_bumpN);
            nl = new Vector3(Mathf.Abs(nl.x), Mathf.Abs(nl.y), Mathf.Abs(nl.z));
            Vector3 k = Vector3.one - nl * sq + (Vector3.one - nl) * (0.5f * sq);
            _bodyT.localScale = Vector3.Scale(_bodyScaleWas, k);
            Vector3 axis = Vector3.Cross(_bumpN, Vector3.up);
            if (axis.sqrMagnitude < 1e-4f) axis = transform.right;
            _bodyT.rotation = Quaternion.AngleAxis(wob, axis.normalized) * _bodyT.rotation;
            _bodyScaleSet = _bodyT.localScale;
            _bodyRotSet = _bodyT.localRotation;
            _bodyTouched = true;
        }
    }
}
