using System;
using System.Collections.Generic;
using T17.Services;
using T17.UI;
using Team17.Scripts.Services.Input;
using UnityEngine;

namespace DateEverythingAccess
{
    // MOD TUTORIAL. Short spoken lessons that run alongside the game's own tutorial and teach the
    // mod's keys at the moment the player needs them. Each step fires once, in order, when the game
    // reaches the point it is written for. Most steps key off the game's tutorial objective (the
    // signpost text), so a player who loads a save part way through hears the step for where they
    // are, not the ones before it. Steps that depend on something the player does in between
    // (turning on tracking, a message appearing, morning coming round, a popup being closed) only
    // fire after the step before them. H repeats the most recent tutorial message at any time.
    internal sealed partial class AccessibilityWatcher
    {
        private delegate bool TutorialTrigger(AccessibilityWatcher w);

        private sealed class ModTutorialStep
        {
            public string Key;
            // True: may fire straight away even if earlier steps never did (it reads game state).
            // False: only fires as the very next step after the one before it.
            public bool StandsAlone;
            public TutorialTrigger Trigger;
            // Only once the tutorial is under way (some step already spoken), for triggers that
            // could also be true on a later day: the work chats, meeting Betty.
            public bool NeedsStarted;
        }

        private static readonly ModTutorialStep[] ModTutorialSteps =
        {
            new ModTutorialStep { Key = "modtut_start", StandsAlone = true,
                Trigger = w => InHouse() && ObjectiveHas("start your new job at your computer") },
            new ModTutorialStep { Key = "modtut_tracking_on", StandsAlone = false,
                Trigger = w => ObjectTracker.IsTracking && ObjectiveHas("start your new job at your computer") },
            new ModTutorialStep { Key = "modtut_tasks", StandsAlone = true, NeedsStarted = true,
                Trigger = w => WorkspaceChatState(out bool samEnded) && samEnded },
            new ModTutorialStep { Key = "modtut_tasks_done", StandsAlone = false,
                Trigger = w => CustomerChatsFinished() >= 5 },
            new ModTutorialStep { Key = "modtut_phone_messages", StandsAlone = true,
                Trigger = w => InHouse() && ObjectiveHas("message on your phone") },
            new ModTutorialStep { Key = "modtut_leave_office", StandsAlone = true,
                Trigger = w => InHouse() && (ObjectiveHas("leave the office") || ObjectiveHas("leave your office")) },
            new ModTutorialStep { Key = "modtut_package", StandsAlone = true,
                Trigger = w => InHouse() && (ObjectiveHas("check the delivery") || ObjectiveHas("check the package")) },
            new ModTutorialStep { Key = "modtut_dorian", StandsAlone = true,
                Trigger = w => InHouse() && (ObjectiveHas("awaken a door") || ObjectiveHas("talk to dorian")) },
            new ModTutorialStep { Key = "modtut_awaken_phone", StandsAlone = true,
                Trigger = w => InHouse() && ObjectiveHas("awaken your phone") },
            new ModTutorialStep { Key = "modtut_magnifying_glass", StandsAlone = true,
                Trigger = w => InHouse() && ObjectiveHas("locate the magnifying glass") },
            new ModTutorialStep { Key = "modtut_rumors", StandsAlone = true,
                Trigger = w => InHouse() && ObjectiveHas("follow the clue in roomers") },
            new ModTutorialStep { Key = "modtut_bed", StandsAlone = true, NeedsStarted = true,
                Trigger = w => InHouse() && _modTutorialMetBetty },
            new ModTutorialStep { Key = "modtut_morning", StandsAlone = false,
                Trigger = w => w.ModTutorialMorningArrived() },
            new ModTutorialStep { Key = "modtut_finish", StandsAlone = false,
                Trigger = w => w.ModTutorialMessageClosed() },
        };

        private const float ModTutorialPollSeconds = 0.25f;
        // How long a trigger must hold before the step speaks, so the game's own announcement (the
        // new objective, the end of a conversation) is heard first.
        private const float ModTutorialSettleSeconds = 1.0f;

        private int _modTutorialLastFired = -1;
        private int _modTutorialPendingStep = -1;
        private float _modTutorialPendingSince;
        private float _modTutorialNextPoll;
        private string _modTutorialLastMessage;
        private bool _modTutorialSpeaking;
        private string _modTutorialBedDay;
        private bool _modTutorialMessageWasOpen;
        private bool _hKeyWasDown;

        private static bool InHouse()
        {
            return Singleton<GameController>.Instance != null &&
                Singleton<GameController>.Instance.viewState == VIEW_STATE.HOUSE;
        }

        private static bool ObjectiveHas(string phrase)
        {
            return TryGetCurrentTutorialObjectiveText(out string text) && ContainsToken(text, phrase);
        }

        private bool ModTutorialMorningArrived()
        {
            // A new day since the bed step, and back in the house (awake). The day of the week is
            // the reliable signal: the time of day can flicker while the game puts the player to bed.
            string day = CurrentDayName();
            if (string.IsNullOrEmpty(day) || string.IsNullOrEmpty(_modTutorialBedDay)) return false;
            return !string.Equals(day, _modTutorialBedDay, StringComparison.Ordinal) && InHouse();
        }

        private static string CurrentDayName()
        {
            if (Singleton<DayNightCycle>.Instance == null) return null;
            try { return Singleton<DayNightCycle>.Instance.GetDayOfWeek(); }
            catch { return null; }
        }

        // Datables met (awakened) this session, from the meeting card. Betty is the bed.
        private static bool _modTutorialMetBetty;

        internal static void NoteModTutorialMeeting(string internalName)
        {
            if (string.Equals(internalName, "betty", StringComparison.OrdinalIgnoreCase))
                _modTutorialMetBetty = true;
        }

        // The work computer's chats (Sam, Tom, David Most): whether Sam's first conversation has
        // finished. Read from the game's own "chat ended" flag.
        private static System.Reflection.FieldInfo _chatEndedField;
        private static bool _chatEndedFieldResolved;
        private static string _lastWorkspaceStateLog;
        private static readonly Dictionary<string, bool> _workChatEnded = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static int _samChatsEnded;

        private static bool WorkspaceChatState(out bool samEnded)
        {
            samEnded = false;
            if (ChatMaster.Instance == null || ChatMaster.Instance.WorkspaceChats == null) return false;
            if (!_chatEndedFieldResolved)
            {
                _chatEndedFieldResolved = true;
                _chatEndedField = typeof(ParallelChat).GetField("chatEnded",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (_chatEndedField == null && Main.Log != null)
                    Main.Log.LogWarning("Mod tutorial: ParallelChat.chatEnded not found");
            }
            if (_chatEndedField == null) return false;
            var sb = new System.Text.StringBuilder();
            foreach (ParallelChat chat in ChatMaster.Instance.WorkspaceChats)
            {
                if (chat == null || chat.appMessage == null) continue;
                bool ended = _chatEndedField.GetValue(chat) is bool b && b;
                string name = (chat.appMessage.Name ?? string.Empty).Trim();
                sb.Append(name).Append(ended ? "=ended " : "=open ");
                bool isSam = name.Equals("Sam", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Sam ", StringComparison.OrdinalIgnoreCase);
                bool wasEnded = _workChatEnded.TryGetValue(name, out bool prev) && prev;
                _workChatEnded[name] = ended;
                if (ended && !wasEnded && isSam) _samChatsEnded++;
            }
            samEnded = _samChatsEnded > 0;
            string state = sb.ToString();
            if (state != _lastWorkspaceStateLog && Main.Log != null)
            {
                _lastWorkspaceStateLog = state;
                Main.Log.LogInfo("Mod tutorial: work chats " + state + "| Sam finished " + _samChatsEnded);
            }
            return true;
        }

        // The customers: their own list on the work screen (the game calls it Canopy), opened with
        // R. Every conversation there that finishes counts; after the fifth, the Q tip.
        private static readonly Dictionary<ParallelChat, bool> _customerChatEnded = new Dictionary<ParallelChat, bool>();
        private static int _customerListChatsEnded;
        private static string _lastCustomerStateLog;

        private static int CustomerChatsFinished()
        {
            if (ChatMaster.Instance == null || ChatMaster.Instance.CanopyChats == null || _chatEndedField == null)
                return _customerListChatsEnded;
            var sb = new System.Text.StringBuilder();
            foreach (ParallelChat chat in ChatMaster.Instance.CanopyChats)
            {
                if (chat == null) continue;
                bool ended = _chatEndedField.GetValue(chat) is bool b && b;
                bool wasEnded = _customerChatEnded.TryGetValue(chat, out bool prev) && prev;
                _customerChatEnded[chat] = ended;
                if (ended && !wasEnded) _customerListChatsEnded++;
                string name = chat.appMessage != null ? (chat.appMessage.Name ?? string.Empty).Trim() : "?";
                sb.Append(name).Append(ended ? "=ended " : "=open ");
            }
            string state = sb.ToString();
            if (state != _lastCustomerStateLog && Main.Log != null)
            {
                _lastCustomerStateLog = state;
                Main.Log.LogInfo("Mod tutorial: customer chats " + state + "| finished " + _customerListChatsEnded);
            }
            return _customerListChatsEnded;
        }

        // The "glasses off mode" message: any game popup or dialog box that opens after the morning
        // step and is then closed.
        private bool ModTutorialMessageClosed()
        {
            bool open = TryBuildPopupAnnouncement(out _) || TryBuildUIDialogAnnouncement(out _);
            if (open)
            {
                _modTutorialMessageWasOpen = true;
                return false;
            }
            return _modTutorialMessageWasOpen;
        }

        private void UpdateModTutorial()
        {
            if (_modTutorialLastFired >= ModTutorialSteps.Length - 1) return;
            if (UnityEngine.Time.unscaledTime < _modTutorialNextPoll) return;
            _modTutorialNextPoll = UnityEngine.Time.unscaledTime + ModTutorialPollSeconds;

            // The latest step that is ready wins; earlier ones the player has moved past are skipped.
            int ready = -1;
            try
            {
                for (int i = ModTutorialSteps.Length - 1; i > _modTutorialLastFired; i--)
                {
                    ModTutorialStep step = ModTutorialSteps[i];
                    if (!step.StandsAlone && i != _modTutorialLastFired + 1) continue;
                    if (step.NeedsStarted && _modTutorialLastFired < 0) continue;
                    if (step.Trigger(this)) { ready = i; break; }
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("Mod tutorial check failed: " + ex.Message);
                return;
            }

            if (ready < 0)
            {
                _modTutorialPendingStep = -1;
                return;
            }
            if (ready != _modTutorialPendingStep)
            {
                _modTutorialPendingStep = ready;
                _modTutorialPendingSince = UnityEngine.Time.unscaledTime;
                return;
            }
            if (UnityEngine.Time.unscaledTime - _modTutorialPendingSince < ModTutorialSettleSeconds)
                return;

            FireModTutorialStep(ready);
        }

        private void FireModTutorialStep(int index)
        {
            ModTutorialStep step = ModTutorialSteps[index];
            _modTutorialLastFired = index;
            _modTutorialPendingStep = -1;
            if (step.Key == "modtut_bed")
                _modTutorialBedDay = CurrentDayName();
            if (step.Key == "modtut_morning")
                _modTutorialMessageWasOpen = TryBuildPopupAnnouncement(out _) || TryBuildUIDialogAnnouncement(out _);

            _modTutorialLastMessage = Loc.Get(step.Key);
            if (Main.Log != null) Main.Log.LogInfo("Mod tutorial step " + (index + 1) + "/" + ModTutorialSteps.Length + ": " + step.Key);
            SpeakModTutorial(_modTutorialLastMessage, interrupt: false);
        }

        private void SpeakModTutorial(string text, bool interrupt)
        {
            _modTutorialSpeaking = true;
            try { ScreenReader.Say(text, interrupt: interrupt); }
            finally { _modTutorialSpeaking = false; }
        }

        // H: say the latest tutorial message again.
        private void HandleModTutorialRepeat(bool pressed)
        {
            if (!pressed) return;
            if (string.IsNullOrEmpty(_modTutorialLastMessage))
            {
                SpeakModTutorial(Loc.Get("modtut_none"), interrupt: true);
                return;
            }
            SpeakModTutorial(_modTutorialLastMessage, interrupt: true);
        }
    }
}
