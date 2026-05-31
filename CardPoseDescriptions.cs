using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx;

namespace DateEverythingAccess
{
    /// <summary>
    /// Loads pre-authored, per-language descriptions of the fullscreen datable pose cards
    /// (first-meet <c>AwakenSplashScreen</c> + Love/Hate/Friend/Realized <c>ResultSplashScreen</c>)
    /// and looks them up by the card's <c>(internalName, pose, expression)</c> identity.
    ///
    /// Descriptions live in external files <c>card_pose_descriptions.&lt;lang&gt;.json</c> under the
    /// plugin folder so they can be edited/translated without recompiling the mod. The active
    /// language tracks the game's text-language setting via <see cref="Loc.CurrentLanguage"/>;
    /// lookups fall back to English when the active-language file or key is missing, mirroring
    /// <see cref="Loc.Get"/>.
    /// </summary>
    internal static class CardPoseDescriptions
    {
        private const string FallbackLanguage = "en";

        // Cache for the currently-loaded active language plus the English fallback. We reload when
        // Loc's selected language changes (cheap string compare per lookup) instead of hooking the
        // ad-hoc RefreshLanguage call sites.
        private static readonly object _lock = new object();
        private static string _loadedLanguage;
        private static Dictionary<string, string> _active;
        private static Dictionary<string, string> _english;

        /// <summary>
        /// Builds the lookup key for a card's stable identity. Uses the enum names directly so the
        /// JSON keys read like <c>maggie|love|flirt</c> / <c>penelope|love|joy</c>.
        /// </summary>
        internal static string BuildKey(string internalName, E_General_Poses pose, E_Facial_Expressions expression)
        {
            string name = (internalName ?? string.Empty).Trim().ToLowerInvariant();
            return name + "|" + pose + "|" + expression;
        }

        /// <summary>
        /// Tries to resolve a description for the given card identity. Returns the active-language
        /// text if present, else the English text, else false (caller stays silent).
        /// </summary>
        internal static bool TryGet(string internalName, E_General_Poses pose, E_Facial_Expressions expression, out string description)
        {
            description = null;
            if (string.IsNullOrEmpty(internalName))
                return false;

            string key = BuildKey(internalName, pose, expression);

            lock (_lock)
            {
                EnsureLoaded();

                if (_active != null && _active.TryGetValue(key, out description) && !string.IsNullOrWhiteSpace(description))
                    return true;

                if (_english != null && _english.TryGetValue(key, out description) && !string.IsNullOrWhiteSpace(description))
                    return true;
            }

            description = null;
            return false;
        }

        // Caller holds _lock.
        private static void EnsureLoaded()
        {
            string lang = FallbackLanguage;
            try
            {
                lang = Loc.CurrentLanguage;
            }
            catch
            {
                // Loc not ready yet — fall back to English.
            }

            if (string.IsNullOrEmpty(lang))
                lang = FallbackLanguage;

            if (string.Equals(lang, _loadedLanguage, StringComparison.OrdinalIgnoreCase) && _active != null)
                return;

            _english = LoadLanguage(FallbackLanguage);
            _active = string.Equals(lang, FallbackLanguage, StringComparison.OrdinalIgnoreCase)
                ? _english
                : LoadLanguage(lang);
            _loadedLanguage = lang;
        }

        private static Dictionary<string, string> LoadLanguage(string lang)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(Paths.PluginPath, "card_pose_descriptions." + lang + ".json");

            if (!File.Exists(path))
            {
                if (Main.Log != null)
                    Main.Log.LogWarning("CardPoseDescriptions: file missing at " + path);
                return result;
            }

            try
            {
                CardPoseDoc doc;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(CardPoseDoc));
                    doc = serializer.ReadObject(stream) as CardPoseDoc;
                }

                if (doc != null && doc.entries != null)
                {
                    foreach (CardPoseEntry entry in doc.entries)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.key))
                            continue;
                        result[entry.key.Trim()] = entry.text;
                    }
                }

                if (Main.Log != null)
                    Main.Log.LogInfo("CardPoseDescriptions: loaded " + result.Count + " entries from " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                if (Main.Log != null)
                    Main.Log.LogError("CardPoseDescriptions: parse failed at " + path + " ex=" + ex.Message);
            }

            return result;
        }

        // DataContractJsonSerializer can't deserialize arbitrary-key dictionaries, so the file is an
        // array of { key, text } records under "entries":
        //   { "entries": [ { "key": "maggie|neutral|neutral", "text": "..." }, ... ] }
        // Fields are populated by DataContractJsonSerializer via reflection, so the compiler's
        // "never assigned" warning (CS0649) is a false positive here.
#pragma warning disable 0649
        [DataContract]
        private class CardPoseDoc
        {
            [DataMember] public CardPoseEntry[] entries;
        }

        [DataContract]
        private class CardPoseEntry
        {
            [DataMember] public string key;
            [DataMember] public string text;
        }
#pragma warning restore 0649
    }
}
