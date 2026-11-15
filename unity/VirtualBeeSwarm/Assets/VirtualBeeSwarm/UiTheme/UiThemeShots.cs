using System;
using System.IO;
using UnityEngine;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// Dev only (desktop / capture builds, never on unless VBS_UI_SHOTS=&lt;folder&gt; is set): every VBS_UI_SHOTS_EVERY
    /// frames (default 30) it renders the table from the player's eye (the union card, the price tags, the rocket sign)
    /// and writes JPGs, because the capture tool's POV looks over the table edge and never sees the card.
    /// </summary>
    public sealed class UiThemeShots : MonoBehaviour
    {
        string _dir;
        int _every = 30;
        Camera _cam;
        RenderTexture _rt;
        Texture2D _tex;

        public static void InstallIfAsked()
        {
            string dir = null;
            try { dir = Environment.GetEnvironmentVariable("VBS_UI_SHOTS"); } catch (Exception) { }
            if (string.IsNullOrEmpty(dir) || FindAnyObjectByType<UiThemeShots>() != null) return;
            var go = new GameObject("[VBS] UiThemeShots");
            var s = go.AddComponent<UiThemeShots>();
            s._dir = dir;
            if (int.TryParse(Environment.GetEnvironmentVariable("VBS_UI_SHOTS_EVERY"), out int n) && n > 0) s._every = n;
            Directory.CreateDirectory(dir);
            Debug.Log("[VBS][UiTheme] shots every " + s._every + " frames -> " + dir);
        }

        void LateUpdate()
        {
            if (Time.frameCount % _every != 0) return;
            var main = Camera.main;
            if (main == null) return;
            if (_cam == null)
            {
                var go = new GameObject("ShotCam");
                go.transform.SetParent(transform, false);
                _cam = go.AddComponent<Camera>();
                _cam.CopyFrom(main);
                _cam.enabled = false;
                _rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
                _tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                _cam.targetTexture = _rt;
                _cam.aspect = 1280f / 720f;
            }
            Transform anchor = StickerDirector.Instance != null && StickerDirector.Instance.Card != null ? StickerDirector.Instance.Card.transform.parent : null;
            Vector3 eye = main.transform.position;
            Vector3 table = anchor != null ? anchor.TransformPoint(new Vector3(-0.17f, 0.12f, 0.04f)) : eye + main.transform.forward;
            Shoot("table", eye, table, 50f);
            var card = StickerDirector.Instance != null ? StickerDirector.Instance.Card : null;
            if (card != null && card.Visible) Shoot("card", eye, card.transform.position + Vector3.up * 0.02f, 24f);
            if (anchor != null) Shoot("arc", eye, anchor.TransformPoint(new Vector3(0f, 0.36f, -0.1f)), 44f);
        }

        void Shoot(string view, Vector3 eye, Vector3 target, float fov)
        {
            _cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            _cam.fieldOfView = fov;
            _cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
            RenderTexture.active = prev;
            try { File.WriteAllBytes(Path.Combine(_dir, view + "_" + Time.frameCount.ToString("000000") + ".jpg"), _tex.EncodeToJPG(88)); }
            catch (Exception e) { Debug.LogWarning("[VBS][UiTheme] shot failed: " + e.Message); enabled = false; }
        }

        void OnDestroy()
        {
            if (_rt != null) _rt.Release();
        }
    }
}
