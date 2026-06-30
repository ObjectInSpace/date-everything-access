using HarmonyLib;

namespace DateEverythingAccess
{
    /// <summary>
    /// Speaks a pre-authored description of the fullscreen datable pose cards when they appear:
    /// the first-meet <see cref="AwakenSplashScreen"/> and the Love/Hate/Friend/Realized
    /// <see cref="ResultSplashScreen"/>. Each card shows one art sprite identified by
    /// <c>(internalName, pose, expression)</c>; we re-derive that identity from the same inputs the
    /// game uses and hand it to <see cref="AccessibilityWatcher.RequestCardPoseAnnouncement"/>,
    /// which looks it up in <see cref="CardPoseDescriptions"/> and speaks it after the card stinger.
    /// </summary>
    [HarmonyPatch]
    internal static class CardSplashPatches
    {
        // First-meet card. DateADex.OpenAwakenScreen has already baked the per-character first-meet
        // specials (diana shock, penelope love+joy, dorian front/back/tiny/trap, connie folded, gun)
        // into PoseToUse/ExpressionToUse, so the args ARE the displayed identity.
        [HarmonyPatch(typeof(AwakenSplashScreen), "Initialize")]
        [HarmonyPostfix]
        private static void OnAwakenInitialize(
            DateADexEntry entry,
            string internalName,
            bool forceCustomCharName,
            E_General_Poses PoseToUse,
            E_Facial_Expressions ExpressionToUse)
        {
            // Mirror AwakenSplashScreen.Initialize's own sprite source so our description matches the art:
            // when forceCustomCharName is set the game loads the sprite from the internalName ARG, not the
            // entry — that's the variant identity (clarence, volt, …) which differs from entry.internalName
            // (dirk, eddie). Otherwise the entry's internalName is the displayed identity.
            string name = (!forceCustomCharName && entry != null && !string.IsNullOrEmpty(entry.internalName))
                ? entry.internalName
                : internalName;
            // First-meet card: the descriptions are authored for these, keyed neutral|neutral, so allow the
            // neutral fallback to cover the per-character non-neutral first-meet poses.
            AccessibilityWatcher.RequestCardPoseAnnouncement(name, PoseToUse, ExpressionToUse, allowNeutralFallback: true);
        }

        // Ending card. ResultSplashScreen.Initialize loads the neutral sprite first, then the
        // animator swaps in _relationshipStatusSprite — the pose we actually want to describe. We
        // re-derive that (name, pose, expression) here, mirroring ResultSplashScreen.Initialize.
        [HarmonyPatch(typeof(ResultSplashScreen), "Initialize")]
        [HarmonyPostfix]
        private static void OnResultInitialize(
            DateADexEntry entry,
            RelationshipStatus status,
            string internalName,
            string charName)
        {
            string name = (entry != null && !string.IsNullOrEmpty(entry.internalName)) ? entry.internalName : internalName;
            if (!ResolveEndingPose(status, name, charName, out string artName, out E_General_Poses pose, out E_Facial_Expressions expr))
                return;

            // Ending card: no ending-pose descriptions are authored yet, so require an exact key (no neutral
            // fallback). Today this is always a miss and the card stays silent — by design — rather than
            // re-reading the character's first-meet appearance blurb. Authoring ending descriptions is a
            // future content task; when added, they'll match here without further code changes.
            AccessibilityWatcher.RequestCardPoseAnnouncement(artName, pose, expr, allowNeutralFallback: false);
        }

        /// <summary>
        /// Reproduces the end-pose selection in <c>ResultSplashScreen.Initialize</c>: the same
        /// status/name/ink-var branch that builds <c>_relationshipStatusSprite</c>. <paramref name="artName"/>
        /// is the name the game's sprite loader uses (so it matches the description key), which differs
        /// from <paramref name="internalName"/> for the dorian variants, timmy→tim, jonwick→scandalabra.
        /// Returns false for statuses that show no end-pose sprite (Single).
        /// </summary>
        private static bool ResolveEndingPose(
            RelationshipStatus status,
            string internalName,
            string charName,
            out string artName,
            out E_General_Poses pose,
            out E_Facial_Expressions expr)
        {
            artName = internalName;
            pose = E_General_Poses.neutral;
            expr = E_Facial_Expressions.neutral;

            // scandalabra collapses to the jonwick branch (and "scandalabra" art) when staying jon.
            if (internalName == "scandalabra" && GetBoolVar("stayjon"))
                internalName = "jonwick";

            switch (status)
            {
                case RelationshipStatus.Hate:
                    switch (internalName)
                    {
                        case "lucinda":
                            artName = "lucinda";
                            switch (GetStringVar("lucinda_final"))
                            {
                                case "lust": pose = E_General_Poses.lust; expr = E_Facial_Expressions.tsundere; break;
                                case "loving": pose = E_General_Poses.hate; expr = E_Facial_Expressions.crosstsundere; break;
                                case "limitless": pose = E_General_Poses.friend; expr = E_Facial_Expressions.tsundere; break;
                                case "lucid": pose = E_General_Poses.love; expr = E_Facial_Expressions.tsundere; break;
                                default: pose = E_General_Poses.neutral; expr = E_Facial_Expressions.tsundere; break;
                            }
                            return true;
                        case "connie":
                            artName = "connie";
                            if (charName == "luna") { pose = E_General_Poses.folded; expr = E_Facial_Expressions.tsundere; }
                            else { pose = E_General_Poses.gun; expr = E_Facial_Expressions.shout; }
                            return true;
                        case "jonwick":
                            artName = "scandalabra"; pose = E_General_Poses.sulk; expr = E_Facial_Expressions.neutral; return true;
                        case "timmy":
                            artName = "tim"; pose = E_General_Poses.feral; expr = E_Facial_Expressions.tsundere; return true;
                        case "front":
                            artName = "dorian"; pose = E_General_Poses.front; expr = E_Facial_Expressions.angry; return true;
                        case "tiny":
                            artName = "dorian"; pose = E_General_Poses.tiny; expr = E_Facial_Expressions.angry; return true;
                        case "trap":
                            artName = "dorian"; pose = E_General_Poses.trap; expr = E_Facial_Expressions.angry; return true;
                        case "back":
                            artName = "dorian"; pose = E_General_Poses.back; expr = E_Facial_Expressions.neutral; return true;
                        default:
                            pose = E_General_Poses.hate; expr = E_Facial_Expressions.angry; return true;
                    }

                case RelationshipStatus.Love:
                    switch (internalName)
                    {
                        case "shadow":
                            pose = E_General_Poses.skips; expr = E_Facial_Expressions.flirt; return true;
                        case "wallace":
                            pose = E_General_Poses.hate; expr = E_Facial_Expressions.happy; return true;
                        case "connie":
                            artName = "connie";
                            if (charName == "luna") { pose = E_General_Poses.folded; expr = E_Facial_Expressions.smirk; }
                            else { pose = E_General_Poses.love; expr = E_Facial_Expressions.joy; }
                            return true;
                        case "lucinda":
                            switch (GetStringVar("lucinda_final"))
                            {
                                case "lust": pose = E_General_Poses.lust; expr = E_Facial_Expressions.joy; break;
                                case "loving": pose = E_General_Poses.hate; expr = E_Facial_Expressions.flirt; break;
                                case "limitless": pose = E_General_Poses.friend; expr = E_Facial_Expressions.smirk; break;
                                case "lucid": pose = E_General_Poses.love; expr = E_Facial_Expressions.sad; break;
                                default: pose = E_General_Poses.neutral; expr = E_Facial_Expressions.happy; break;
                            }
                            return true;
                        case "jonwick":
                            artName = "scandalabra"; pose = E_General_Poses.sulk; expr = E_Facial_Expressions.neutral; return true;
                        case "timmy":
                            artName = "tim"; pose = E_General_Poses.nya; expr = E_Facial_Expressions.flirt; return true;
                        case "front":
                            artName = "dorian"; pose = E_General_Poses.front; expr = E_Facial_Expressions.flirt; return true;
                        case "tiny":
                            artName = "dorian"; pose = E_General_Poses.tiny; expr = E_Facial_Expressions.flirt; return true;
                        case "trap":
                            artName = "dorian"; pose = E_General_Poses.trap; expr = E_Facial_Expressions.flirt; return true;
                        case "back":
                            artName = "dorian"; pose = E_General_Poses.back; expr = E_Facial_Expressions.neutral; return true;
                        default:
                            pose = E_General_Poses.love; expr = E_Facial_Expressions.flirt; return true;
                    }

                case RelationshipStatus.Friend:
                    switch (internalName)
                    {
                        case "shadow":
                            pose = E_General_Poses.skips; expr = E_Facial_Expressions.happy; return true;
                        case "dorian":
                            pose = E_General_Poses.neutral; expr = E_Facial_Expressions.joy; return true;
                        case "connie":
                            artName = "connie";
                            if (charName == "luna") { pose = E_General_Poses.gun; expr = E_Facial_Expressions.happy; }
                            else { pose = E_General_Poses.folded; expr = E_Facial_Expressions.smirk; }
                            return true;
                        case "lucinda":
                            switch (GetStringVar("lucinda_final"))
                            {
                                case "lust": pose = E_General_Poses.lust; expr = E_Facial_Expressions.happy; break;
                                case "loving": pose = E_General_Poses.hate; expr = E_Facial_Expressions.joy; break;
                                case "limitless": pose = E_General_Poses.friend; expr = E_Facial_Expressions.joy; break;
                                case "lucid": pose = E_General_Poses.love; expr = E_Facial_Expressions.smirk; break;
                                default: pose = E_General_Poses.neutral; expr = E_Facial_Expressions.joy; break;
                            }
                            return true;
                        case "jonwick":
                            artName = "scandalabra"; pose = E_General_Poses.sulk; expr = E_Facial_Expressions.neutral; return true;
                        case "timmy":
                            artName = "tim"; pose = E_General_Poses.nya; expr = E_Facial_Expressions.joy; return true;
                        case "front":
                            artName = "dorian"; pose = E_General_Poses.front; expr = E_Facial_Expressions.joy; return true;
                        case "tiny":
                            artName = "dorian"; pose = E_General_Poses.tiny; expr = E_Facial_Expressions.joy; return true;
                        case "trap":
                            artName = "dorian"; pose = E_General_Poses.trap; expr = E_Facial_Expressions.joy; return true;
                        case "back":
                            artName = "dorian"; pose = E_General_Poses.back; expr = E_Facial_Expressions.neutral; return true;
                        default:
                            pose = E_General_Poses.friend; expr = E_Facial_Expressions.joy; return true;
                    }

                case RelationshipStatus.Realized:
                    if (internalName == "sinclaire" && GetBoolVar("sinclaire_turtle"))
                    {
                        artName = "sinclaire"; pose = E_General_Poses.turtle; expr = E_Facial_Expressions.happy; return true;
                    }
                    if (internalName == "jonwick")
                    {
                        artName = "scandalabra"; pose = E_General_Poses.wick; expr = E_Facial_Expressions.think; return true;
                    }
                    if (internalName == "rebel")
                    {
                        artName = "rebel"; pose = E_General_Poses.realized; expr = E_Facial_Expressions.think; return true;
                    }
                    pose = E_General_Poses.realized; expr = E_Facial_Expressions.joy; return true;

                default:
                    // Single (and any non-pose status) shows no end-pose sprite.
                    return false;
            }
        }

        private static string GetStringVar(string name)
        {
            try
            {
                return Singleton<InkController>.Instance != null
                    ? Singleton<InkController>.Instance.GetVariable(name)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool GetBoolVar(string name)
        {
            // Read via the string API to avoid referencing the Ink-Libraries assembly. Ink bools
            // stringify as "true"/"false" (or "1"/"0" on older ink), so accept both.
            string value = GetStringVar(name);
            if (string.IsNullOrEmpty(value))
                return false;
            return value.Equals("true", System.StringComparison.OrdinalIgnoreCase) || value == "1";
        }
    }
}
