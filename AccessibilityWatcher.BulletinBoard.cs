using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace DateEverythingAccess
{
    // BULLETIN BOARD (first step: find out how it is built). The office bulletin board collects
    // photos of datables, with a little flavour text each, as the game goes on (the Ink story
    // counts them in a variable called "bulletins"). Before the mod can read them it has to know
    // where the photos and their text live, so the first time the player looks at the board in a
    // session (and again whenever the photo count changes), the board's whole make-up is written
    // to the log: every piece, whether it is showing, and any text, image or material names on it.
    internal sealed partial class AccessibilityWatcher
    {
        private float _bulletinNextCheck;
        private string _bulletinDumpedFor;   // "bulletins" value at the last dump, or null

        private void DiagnoseBulletinBoardIfNeeded()
        {
            if (Time.unscaledTime < _bulletinNextCheck) return;
            _bulletinNextCheck = Time.unscaledTime + 0.5f;
            try
            {
                InteractableManager manager = Singleton<InteractableManager>.Instance;
                if (manager == null || manager.activeObject == null) return;
                Transform board = FindNamedAncestor(manager.activeObject.transform, "BulletinBoard");
                if (board == null) return;
                string count = GetInkVariableString("bulletins") ?? "?";
                if (string.Equals(count, _bulletinDumpedFor, StringComparison.Ordinal)) return;
                _bulletinDumpedFor = count;
                DumpBulletinBoard(board, count);
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("BulletinBoard diagnostic failed: " + ex.Message);
            }
        }

        // ---- reading the photos ----
        // The board holds one polaroid per datable per ending (groups "love", "friend", ...), each
        // hidden until earned and each with its own examine text: the flavour text. While the
        // board is selected, PageDown / PageUp step through the photos that are up: the mod names
        // the photo and has the game show its examine text, which the mod then reads as usual.
        private int _bulletinIndex = -1;
        private Transform _bulletinBoardSeen;
        private float _bulletinSeenUntil;
        private static System.Reflection.MethodInfo _showExamineMethod;
        private static bool _showExamineLooked;
        private static bool _bulletinFieldsLogged;

        // True when the press was for the bulletin board (and has been handled).
        private bool HandleBulletinBoardPaging(bool next)
        {
            Transform board = null;
            InteractableManager manager = Singleton<InteractableManager>.Instance;
            if (manager != null && manager.activeObject != null)
                board = FindNamedAncestor(manager.activeObject.transform, "BulletinBoard");
            if (board != null)
            {
                _bulletinBoardSeen = board;
                _bulletinSeenUntil = Time.unscaledTime + 20f;
            }
            else if (_bulletinBoardSeen != null && Time.unscaledTime < _bulletinSeenUntil)
            {
                board = _bulletinBoardSeen;   // still reading it (the examine panel is up)
            }
            if (board == null)
                return false;

            List<ObjectExamine> photos = CollectBulletinPhotos(board);
            if (photos.Count == 0)
            {
                ScreenReader.Say(Loc.Get("bulletin_none"));
                return true;
            }
            if (_bulletinIndex < 0 || _bulletinIndex >= photos.Count)
                _bulletinIndex = next ? 0 : photos.Count - 1;
            else
                _bulletinIndex = next ? (_bulletinIndex + 1) % photos.Count : (_bulletinIndex + photos.Count - 1) % photos.Count;
            _bulletinSeenUntil = Time.unscaledTime + 20f;

            ObjectExamine photo = photos[_bulletinIndex];
            string group = BulletinGroupLabel(photo.transform.parent != null ? photo.transform.parent.name : string.Empty);
            string who = BulletinPhotoName(photo.gameObject.name);
            ScreenReader.Say(Loc.Get("bulletin_photo", group, who, _bulletinIndex + 1, photos.Count));
            ShowPhotoExamine(photo);
            return true;
        }

        private static List<ObjectExamine> CollectBulletinPhotos(Transform board)
        {
            var photos = new List<ObjectExamine>();
            // Only the photos the game has put up (a photo goes up when its datable is realized).
            foreach (ObjectExamine ex in board.GetComponentsInChildren<ObjectExamine>(false))
            {
                if (ex == null || ex.transform == board || !ex.gameObject.activeInHierarchy) continue;
                if (ex.GetComponent<SpriteRenderer>() == null) continue;   // the photos are sprites
                photos.Add(ex);
            }
            photos.Sort((a, b) =>
            {
                int ga = BulletinGroupOrder(a.transform.parent != null ? a.transform.parent.name : "");
                int gb = BulletinGroupOrder(b.transform.parent != null ? b.transform.parent.name : "");
                if (ga != gb) return ga.CompareTo(gb);
                return string.Compare(a.gameObject.name, b.gameObject.name, StringComparison.OrdinalIgnoreCase);
            });
            return photos;
        }

        // Photo names are the short character name ("skylar"); the save keys characters by
        // internal name ("skylar_specs"). Matched against the internal names in the scene.
        private static Dictionary<string, string> _bulletinInternalNames;

        private static string BulletinInternalName(string shortName)
        {
            if (_bulletinInternalNames == null)
            {
                _bulletinInternalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (InteractableObj io in UnityEngine.Object.FindObjectsOfType<InteractableObj>())
                {
                    string internalName = io != null ? io.InternalName() : null;
                    if (string.IsNullOrEmpty(internalName)) continue;
                    int cut = internalName.IndexOf('_');
                    string key = cut > 0 ? internalName.Substring(0, cut) : internalName;
                    if (!_bulletinInternalNames.TryGetValue(key, out string existing) || internalName.Length < existing.Length)
                        _bulletinInternalNames[key] = internalName;
                }
            }
            if (_bulletinInternalNames.TryGetValue(shortName, out string found)) return found;
            return null;
        }

        private static int BulletinGroupOrder(string group)
        {
            switch ((group ?? "").ToLowerInvariant())
            {
                case "love": return 0;
                case "friend": return 1;
                case "hate": return 2;
                default: return 3;
            }
        }

        private static string BulletinGroupLabel(string group)
        {
            switch ((group ?? "").ToLowerInvariant())
            {
                case "love": return Loc.Get("bulletin_group_love");
                case "friend": return Loc.Get("bulletin_group_friend");
                case "hate": return Loc.Get("bulletin_group_hate");
                default: return FriendlyRoomName(group);
            }
        }

        private static string BulletinPhotoName(string objectName)
        {
            Save save = Singleton<Save>.Instance;
            string internalName = BulletinInternalName(objectName) ?? objectName;
            if (save != null && save.TryGetNameByInternalName(internalName, out string display) &&
                !string.IsNullOrWhiteSpace(display))
                return NormalizeIdentifierName(display);
            return FriendlyRoomName(objectName.Replace('_', ' '));
        }

        private static void ShowPhotoExamine(ObjectExamine photo)
        {
            try
            {
                if (!_showExamineLooked)
                {
                    _showExamineLooked = true;
                    _showExamineMethod = typeof(ObjectExamine).GetMethod("ShowExamine",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                }
                if (!_bulletinFieldsLogged && Main.Log != null)
                {
                    _bulletinFieldsLogged = true;
                    foreach (System.Reflection.FieldInfo f in typeof(ObjectExamine).GetFields(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        object v = null;
                        try { v = f.GetValue(photo); } catch { }
                        Main.Log.LogInfo("[bulletin] ObjectExamine." + f.Name + " (" + f.FieldType.Name + ") = " + (v ?? "null"));
                    }
                }
                object result = _showExamineMethod != null ? _showExamineMethod.Invoke(photo, null) : null;
                bool shown = ExamineController.Instance != null && ExamineController.Instance.isShown;
                if (!shown)
                {
                    // The game didn't open its examine panel (a photo it hasn't put up yet): read the
                    // photo's own text directly, if it carries readable prose.
                    foreach (System.Reflection.FieldInfo f in typeof(ObjectExamine).GetFields(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (f.FieldType != typeof(string)) continue;
                        string text = f.GetValue(photo) as string;
                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 20 && text.IndexOf(' ') > 0)
                        {
                            ScreenReader.Say(NormalizeText(text), interrupt: false, rememberAsRepeatable: true);
                            break;
                        }
                    }
                }
                if (Main.Log != null)
                    Main.Log.LogInfo("[bulletin] ShowExamine on " + photo.gameObject.name + " -> " +
                        (_showExamineMethod == null ? "method not found" : (result ?? "done").ToString()));
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("[bulletin] ShowExamine failed: " + ex.Message);
            }
        }

        private static Transform FindNamedAncestor(Transform t, string name)
        {
            for (int depth = 0; t != null && depth < 16; depth++, t = t.parent)
                if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        private static void DumpBulletinBoard(Transform board, string count)
        {
            if (Main.Log == null) return;
            var sb = new StringBuilder();
            sb.Append("[bulletin] board=").Append(RuntimeBlockerProbe.PathOf(board.gameObject))
              .Append(" bulletins=").Append(count)
              .Append(" realized=").Append(GetInkVariableString("realized") ?? "?");
            Main.Log.LogInfo(sb.ToString());
            int lines = 0;
            DumpTransform(board, 0, ref lines);

            // Photos may also live elsewhere in the scene and be placed on the board at runtime.
            int extra = 0;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.gameObject.scene.name == null || t.IsChildOf(board)) continue;
                string n = t.name;
                if (n.IndexOf("bulletin", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("polaroid", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("photo", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Main.Log.LogInfo("[bulletin] elsewhere: " + RuntimeBlockerProbe.PathOf(t.gameObject) +
                    " active=" + t.gameObject.activeInHierarchy + Describe(t));
                if (++extra >= 60) break;
            }
            Main.Log.LogInfo("[bulletin] dump done lines=" + lines + " elsewhere=" + extra);
        }

        private static void DumpTransform(Transform t, int depth, ref int lines)
        {
            if (t == null || lines >= 500 || depth > 10) return;
            lines++;
            Main.Log.LogInfo("[bulletin] " + new string(' ', depth * 2) + t.name +
                " active=" + t.gameObject.activeSelf + Describe(t));
            for (int i = 0; i < t.childCount; i++)
                DumpTransform(t.GetChild(i), depth + 1, ref lines);
        }

        // Component types plus any text, image or material names on this piece.
        private static string Describe(Transform t)
        {
            var sb = new StringBuilder();
            var types = new List<string>();
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                types.Add(c.GetType().Name);
                if (c is TMP_Text tmp && !string.IsNullOrWhiteSpace(tmp.text))
                    sb.Append(" text=\"").Append(tmp.text.Replace("\n", " / ")).Append('"');
                else if (c is SpriteRenderer sr && sr.sprite != null)
                    sb.Append(" sprite=").Append(sr.sprite.name);
                else if (c is Renderer r && r.sharedMaterials != null)
                {
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        sb.Append(" mat=").Append(m.name);
                        if (m.mainTexture != null) sb.Append("/tex=").Append(m.mainTexture.name);
                    }
                }
                else if (c is InteractableObj io)
                    sb.Append(" ink=").Append(io.inkFileName ?? "").Append(" main=\"").Append(io.mainText ?? "").Append('"');
            }
            if (types.Count > 0) sb.Insert(0, " [" + string.Join(",", types.ToArray()) + "]");
            return sb.ToString();
        }
    }
}
