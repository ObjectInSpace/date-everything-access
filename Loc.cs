using System.Collections.Generic;
using T17.Services;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Minimal localization helper for mod text.
    /// </summary>
    public static class Loc
    {
        private static bool _initialized;
        private static string _currentLang = "en";

        private static readonly Dictionary<string, string> _german = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _english = new Dictionary<string, string>();

        /// <summary>
        /// Initializes the localization dictionaries and selects the active language.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            InitializeStrings();
            RefreshLanguage();
            _initialized = true;
        }

        /// <summary>
        /// Refreshes the active language from the game's current language setting.
        /// </summary>
        public static void RefreshLanguage()
        {
            _currentLang = GetGameLanguage();
        }

        /// <summary>
        /// Gets a localized string for the provided key.
        /// </summary>
        public static string Get(string key)
        {
            string value;
            string englishValue;

            if (!_initialized)
                Initialize();

            Dictionary<string, string> dict = _currentLang == "de" ? _german : _english;
            if (dict.TryGetValue(key, out value))
                return value;

            if (_english.TryGetValue(key, out englishValue))
                return englishValue;

            return key;
        }

        /// <summary>
        /// Gets a localized formatted string for the provided key.
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
            }
        }

        private static string GetGameLanguage()
        {
            try
            {
                if (Services.GameSettings != null && Services.GameSettings.GetInt("textLanguage", 0) != 0)
                    return "en";
            }
            catch
            {
            }

            return Application.systemLanguage == SystemLanguage.German ? "de" : "en";
        }

        private static void Add(string key, string german, string english)
        {
            _german[key] = german;
            _english[key] = english;
        }

        private static void InitializeStrings()
        {
            Add("mod_loaded",
                "Date Everything Access geladen. Fokus, Dialoge, Bildschirmtexte, Telefon-App-Texte, Raeume, Objekte in der Naehe und Statusaenderungen werden automatisch vorgelesen. F1 fuer Hilfe. Strg+F1 wiederholt die letzte Sprachausgabe. F9 schaltet den Debug-Modus um. Strg+F9 oeffnet die Zugaenglichkeitseinstellungen. Strg+F6 verfolgt das aktuelle Ziel. Strg+Umschalt+F6 oeffnet die Objektliste fuer den aktuellen Raum. Strg+Alt+F6 schaltet den Auto-Lauf zum verfolgten Objekt um.",
                "Date Everything Access loaded. Focused items, dialogue, screen text, phone app text, rooms, nearby objects, and status changes are spoken automatically. F1 for help. Ctrl+F1 repeats the last spoken line. F9 toggles debug mode. Ctrl+F9 opens accessibility settings. Ctrl+F6 tracks the current objective. Ctrl+Shift+F6 opens the current room object list. Ctrl+Alt+F6 toggles auto-walk to the tracked object.");
            Add("help_text",
                "Date Everything Access. Fokus, Dialoge, Bildschirmtexte, Telefon-App-Texte, Raeume, Objekte in der Naehe und Statusaenderungen koennen automatisch vorgelesen werden. F1 fuer Hilfe. Strg+F1 wiederholt die letzte Sprachausgabe. F9 schaltet den Debug-Modus um. Strg+F9 oeffnet die Zugaenglichkeitseinstellungen. Strg+F6 verfolgt das aktuelle Ziel. Strg+Umschalt+F6 oeffnet die Objektliste fuer den aktuellen Raum. Strg+Alt+F6 schaltet den Auto-Lauf zum verfolgten Objekt um.",
                "Date Everything Access. Focused items, dialogue, screen text, phone app text, rooms, nearby objects, and status changes can be spoken automatically. F1 for help. Ctrl+F1 repeats the last spoken line. F9 toggles debug mode. Ctrl+F9 opens accessibility settings. Ctrl+F6 tracks the current objective. Ctrl+Shift+F6 opens the current room object list. Ctrl+Alt+F6 toggles auto-walk to the tracked object.");
            Add("debug_mode_enabled", "Debug-Modus aktiviert.", "Debug mode enabled.");
            Add("debug_mode_enabled_with_mapping_dump",
                "Debug-Modus aktiviert. Aktuelle Eingabebelegungen fuer {0} Geraete wurden ins Protokoll geschrieben.",
                "Debug mode enabled. Current input mappings for {0} devices were written to the log.");
            Add("debug_mode_disabled", "Debug-Modus deaktiviert.", "Debug mode disabled.");
            Add("repeat_last_unavailable", "Noch keine Sprachausgabe zum Wiederholen vorhanden.", "Nothing has been spoken yet.");
            Add("settings_menu_opened", "Zugaenglichkeitseinstellungen geoeffnet.", "Accessibility settings opened.");
            Add("settings_menu_closed", "Zugaenglichkeitseinstellungen geschlossen und gespeichert.", "Accessibility settings closed and saved.");
            Add("settings_menu_item",
                "{0} von {1}: {2}, {3}. Links und rechts aendern den Wert. Enter und Leertaste funktionieren auch. Escape schliesst.",
                "{0} of {1}: {2}, {3}. Left and right change the value. Enter and Space also work. Escape closes.");
            Add("settings_menu_changed", "{0}: {1}", "{0}: {1}");
            Add("settings_value_on", "Ein", "On");
            Add("settings_value_off", "Aus", "Off");
            Add("config_focused_items", "Fokussierte Elemente", "Focused items");
            Add("config_dialogue_text", "Dialogtext", "Dialogue text");
            Add("config_dialogue_choices", "Dialogoptionen", "Dialogue choices");
            Add("config_screen_text", "Bildschirmtexte", "Screen text");
            Add("config_phone_app_text", "Telefon-App-Texte", "Phone app text");
            Add("config_room_changes", "Raumwechsel", "Room changes");
            Add("config_nearby_objects", "Nahe Objekte", "Nearby objects");
            Add("config_status_changes", "Statusaenderungen", "Status changes");
            Add("room_announcement", "Raum: {0}", "Room: {0}");
            Add("nearby_announcement_without_prompt", "In der Naehe: {0}.", "Nearby: {0}.");
            Add("nearby_announcement_with_prompt", "In der Naehe: {0}. {1}.", "Nearby: {0}. {1}.");
            Add("dateviators_equipped", "ausgeruestet", "equipped");
            Add("dateviators_unequipped", "abgesetzt", "unequipped");
            Add("dateviators_state", "Dateviators {0}. {1} Ladungen.", "Dateviators {0}. {1} charges.");
            Add("time_announcement", "Zeit: {0}.", "Time: {0}.");
            Add("collectable_unlocked", "Sammelobjekt freigeschaltet. {0} insgesamt.", "Collectable unlocked. {0} total.");
            Add("dateable_added", "Neue Dateable im Date A Dex hinzugefuegt. {0} getroffen.", "New dateable added to Date A Dex. {0} met.");
            Add("friend_ending_recorded", "Freundschaftsende gespeichert. {0} insgesamt.", "Friend ending recorded. {0} total.");
            Add("love_ending_recorded", "Liebesende gespeichert. {0} insgesamt.", "Love ending recorded. {0} total.");
            Add("hate_ending_recorded", "Hassende gespeichert. {0} insgesamt.", "Hate ending recorded. {0} total.");
            Add("realized_ending_recorded", "Realized-Ende gespeichert. {0} insgesamt.", "Realized ending recorded. {0} total.");
            Add("choice_announcement", "Option {0} von {1}. {2}", "Choice {0} of {1}. {2}");
            Add("apply_display_settings", "Anzeigeeinstellungen anwenden", "Apply display settings");
            Add("new_game_field_name", "Name", "Name");
            Add("new_game_field_town", "Wohnort", "Town");
            Add("new_game_field_favorite_thing", "Lieblingssache", "Favorite thing");
            Add("new_game_field_pronouns", "Pronomen", "Pronouns");
            Add("new_game_field_confirmation", "Bestaetigung", "Confirmation");
            Add("new_game_field_empty", "Leer", "Empty");
            Add("new_game_toggle_selected", "Ausgewaehlt", "Selected");
            Add("new_game_toggle_not_selected", "Nicht ausgewaehlt", "Not selected");
            Add("new_game_pronoun_he_him", "Er/Ihn", "He/Him");
            Add("new_game_pronoun_she_her", "Sie/Ihr", "She/Her");
            Add("new_game_pronoun_they_them", "They/Them", "They/Them");
            Add("phone_app_open_generic", "Telefon-App geoeffnet.", "Phone app open.");
            Add("screen_open", "{0} geoeffnet.", "{0} open.");
            Add("roomers_character", "Charakter: {0}", "Character: {0}");
            Add("roomers_location", "Ort: {0}", "Location: {0}");
            Add("canopy_no_messages", "Canopy. Keine aktiven Nachrichten.", "Canopy. No active messages.");
            Add("canopy_summary_name", "Canopy. {0}", "Canopy. {0}");
            Add("music_no_track_selected", "Kein Titel ausgewaehlt", "No track selected");
            Add("music_playing", "Wird abgespielt", "Playing");
            Add("music_stopped", "Gestoppt", "Stopped");
            Add("music_detail", "Musik. {0}. {1}.", "Music. {0}. {1}.");
            Add("objective_announcement", "Ziel. {0}", "Objective. {0}");
            Add("loading_announcement", "Laden. {0}", "Loading. {0}");
            Add("outcome_announcement", "Ergebnis. {0}", "Outcome. {0}");
            Add("phone_menu_summary", "Telefonmenu. {0} Ladungen. Dateviators {1}.", "Phone menu. {0} charges. Dateviators {1}.");
            Add("roomers_summary_empty", "Roomers.", "Roomers.");
            Add("roomers_summary_screen", "Roomers. {0}.", "Roomers. {0}.");
            Add("dateadex_summary_empty", "Date A Dex.", "Date A Dex.");
            Add("dateadex_summary_item", "Date A Dex. {0}.", "Date A Dex. {0}.");
            Add("dateadex_voice_actor", "Sprechrolle: {0}", "Voice actor: {0}");
            Add("dateadex_likes", "Mag: {0}", "Likes: {0}");
            Add("dateadex_dislikes", "Mag nicht: {0}", "Dislikes: {0}");
            Add("dateadex_pronouns", "Pronomen: {0}", "Pronouns: {0}");
            Add("dateadex_collectables", "Sammelstuecke: {0}", "Collectables: {0}");
            Add("dateadex_button_collectables", "Sammelstuecke", "Collectables");
            Add("dateadex_button_collectables_value", "Sammelstuecke. {0}", "Collectables. {0}");
            Add("dateadex_button_sort", "Sortierung", "Sort");
            Add("dateadex_button_sort_value", "Sortierung. {0}", "Sort. {0}");
            Add("dateadex_button_recipe", "Rezept", "Recipe");
            Add("dateadex_button_show_bio", "Bio anzeigen", "Show bio");
            Add("save_new_slot", "Neuer Speicherstand", "New save");
            Add("button_back", "Zurueck", "Back");
            Add("button_save", "Speichern", "Save");
            Add("button_load", "Laden", "Load");
            Add("button_delete", "Loeschen", "Delete");
            Add("thiscord_summary_empty", "Thiscord.", "Thiscord.");
            Add("thiscord_summary_friend", "Thiscord. {0}.", "Thiscord. {0}.");
            Add("workspace_summary_empty", "Workspace.", "Workspace.");
            Add("workspace_summary_name", "Workspace. {0}.", "Workspace. {0}.");
            Add("music_summary_empty", "Musik.", "Music.");
            Add("music_summary_title", "Musik. {0}.", "Music. {0}.");
            Add("art_summary_empty", "Kunst.", "Art.");
            Add("art_summary_title", "Kunst. {0}.", "Art. {0}.");
            Add("art_detail", "Kunst. {0}. {1}.", "Art. {0}. {1}.");
            Add("specs_summary_stats", "SPECS. Statuswerte.", "SPECS. Stats.");
            Add("specs_summary_glossary", "SPECS. Glossar.", "SPECS. Glossary.");
            Add("specs_button_glossary", "Glossar oeffnen", "Open glossary");
            Add("specs_button_stats", "Zu den Statuswerten zurueck", "Return to stats");
            Add("specs_button_profile", "Zum Profil zurueck", "Return to profile");
            Add("credits_summary", "Credits.", "Credits.");
            Add("language_english", "Englisch", "English");
            Add("language_japanese", "Japanisch", "Japanese");
            Add("settings_summary",
                "Einstellungen. Textsprache {0}. Gesamtlautstaerke {1} Prozent. Musiklautstaerke {2} Prozent.",
                "Settings. Text language {0}. Master volume {1} percent. Music volume {2} percent.");
            Add("unknown_object", "Objekt", "object");
            Add("value_number", "Wert {0}", "Value {0}");
            Add("settings_slider_camera_sensitivity", "Kameraempfindlichkeit. Wert {0}", "Camera sensitivity. Value {0}");
            Add("settings_slider_master_volume", "Gesamtlautstaerke. Wert {0}", "Master volume. Value {0}");
            Add("settings_slider_sfx_volume", "Effektlautstaerke. Wert {0}", "Sound effects volume. Value {0}");
            Add("settings_slider_music_volume", "Musiklautstaerke. Wert {0}", "Music volume. Value {0}");
            Add("settings_slider_voice_volume", "Sprachlautstaerke. Wert {0}", "Voice volume. Value {0}");
            Add("settings_slider_field_of_view", "Sichtfeld. Wert {0}", "Field of view. Value {0}");
            Add("settings_slider_movement_speed", "Bewegungsgeschwindigkeit. Wert {0}", "Movement speed. Value {0}");
            Add("chat_app_only", "{0}", "{0}");
            Add("chat_contact_only", "{0}. {1}", "{0}. {1}");
            Add("chat_latest_message_without_contact", "{0}. Letzte Nachricht. {1}", "{0}. Latest message. {1}");
            Add("chat_latest_message_with_contact", "{0}. {1}. Letzte Nachricht. {2}", "{0}. {1}. Latest message. {2}");
            Add("chat_options", "Optionen. {0}", "Options. {0}");
            Add("navigation_no_objective", "Kein aktuelles Ziel.", "No current objective.");
            Add("navigation_navigating", "Navigiert zu {0}. Naechster Raum: {1}", "Navigating to {0}. Next zone: {1}");
            Add("navigation_tracking", "Verfolge {0}.", "Tracking {0}.");
            Add("navigation_arrived", "Ziel erreicht.", "Arrived at target.");
            Add("navigation_blocked", "Navigation blockiert oder unterbrochen.", "Navigation blocked or interrupted.");
            Add("navigation_select_target_title", "Navigationsziel waehlen", "Select navigation target");
            Add("navigation_room_object_list_title", "Objekte im Raum {0}", "Objects in room {0}");
            Add("navigation_no_room_objects", "Keine verfolgbaren Objekte im aktuellen Raum.", "No trackable objects in the current room.");
            Add("navigation_room_object_option", "Objekt {0} von {1}. {2}", "Object {0} of {1}. {2}");
            Add("navigation_room_object_picker_closed", "Objektliste geschlossen.", "Object list closed.");
            Add("navigation_target_option", "Option {0} von {1}. {2}", "Option {0} of {1}. {2}");
            Add("navigation_target_in_current_room", "Aktueller Raum", "Current room");
            Add("navigation_target_awakened", "Geweckt", "Awakened");
            Add("navigation_autowalk_started", "Auto-Lauf begonnen.", "Auto-walk started.");
            Add("navigation_autowalk_stopped", "Auto-Lauf gestoppt.", "Auto-walk stopped.");
        }
    }
}
