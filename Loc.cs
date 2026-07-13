using System.Collections.Generic;
using T17.Services;

namespace DateEverythingAccess
{
    /// <summary>
    /// Minimal localization helper for mod text.
    /// </summary>
    public static class Loc
    {
        private static bool _initialized;
        private static string _currentLang = "en";

        // The mod supports exactly the languages the game supports (TextLanguage enum: English,
        // Japanese). English is the base; Japanese is populated only for entries that actually
        // differ — format-only templates ("{0}", "{0}: {1}") and proper nouns (SPECS, Canopy,
        // Dateviators, ...) fall through to English. See AddJa / InitializeJapanese.
        private static readonly Dictionary<string, string> _english = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _japanese = new Dictionary<string, string>();

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
        /// The currently selected language code ("en" or "ja"). Other mod systems that ship
        /// their own per-language data (see <c>CardPoseDescriptions</c>) select files with this
        /// so their language tracks the game's text-language setting exactly like <see cref="Get"/>.
        /// </summary>
        public static string CurrentLanguage
        {
            get
            {
                if (!_initialized)
                    Initialize();
                return _currentLang;
            }
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

            // Japanese where provided, English otherwise (base + fallback for untranslated keys).
            if (_currentLang == "ja" && _japanese.TryGetValue(key, out value))
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
            // Match the game's text-language setting exactly. TextLanguage enum: 0 = English,
            // 1 = Japanese. Anything else (or read failure) falls back to English.
            try
            {
                if (Services.GameSettings != null && Services.GameSettings.GetInt("textLanguage", 0) == 1)
                    return "ja";
            }
            catch
            {
            }

            return "en";
        }

        private static void Add(string key, string english)
        {
            _english[key] = english;
        }

        // Japanese override for a key. Call only for entries whose Japanese differs from the
        // English base — untranslated keys correctly fall through to English in Get().
        private static void AddJa(string key, string japanese)
        {
            _japanese[key] = japanese;
        }

        private static void InitializeStrings()
        {
            InitializeEnglish();
            InitializeJapanese();
        }

        private static void InitializeEnglish()
        {
            Add("mod_loaded", "Date Everything Access loaded. Focused items, dialogue, screen text, phone app text, rooms, nearby objects, and status changes are spoken automatically. F1 for help. Backtick repeats the last spoken line. L reports the current room and objects relative to the direction you are facing. Ctrl plus O tracks the current objective. O opens the known objects list. In the list, Up and Down move the selection, Enter selects, Backspace closes, Left and Right change the sort, F toggles this floor only, M cycles the section filter, and D toggles doors only. Alt plus O toggles auto-walk to the selected target. Ctrl+F1 opens accessibility settings.");
            Add("help_text", "Date Everything Access. Focused items, dialogue, screen text, phone app text, rooms, nearby objects, and status changes can be spoken automatically. F1 for help. Backtick repeats the last spoken line. L reports the current room and objects relative to the direction you are facing. Ctrl plus O tracks the current objective. O opens the known objects list. In the list, Up and Down move the selection, Enter selects, Backspace closes, Left and Right change the sort, F toggles this floor only, M cycles the section filter, and D toggles doors only. Alt plus O toggles auto-walk to the selected target. Ctrl+F1 opens accessibility settings.");
            Add("debug_mode_enabled", "Debug mode enabled.");
            Add("debug_mode_enabled_with_mapping_dump", "Debug mode enabled. Current input mappings for {0} devices were written to the log.");
            Add("debug_mode_disabled", "Debug mode disabled.");
            Add("repeat_last_unavailable", "Nothing has been spoken yet.");
            Add("settings_menu_opened", "Accessibility settings opened.");
            Add("settings_menu_closed", "Accessibility settings closed and saved.");
            Add("settings_menu_item", "{0} of {1}: {2}, {3}. Left and right change the value. Enter and Space also work. Backspace closes.");
            Add("settings_menu_changed", "{0}: {1}");
            Add("settings_value_on", "On");
            Add("settings_value_off", "Off");
            Add("config_focused_items", "Focused items");
            Add("config_dialogue_text", "Dialogue text");
            Add("config_dialogue_choices", "Dialogue choices");
            Add("config_screen_text", "Screen text");
            Add("config_phone_app_text", "Phone app text");
            Add("config_room_changes", "Room changes");
            Add("config_nearby_objects", "Nearby objects");
            Add("config_status_changes", "Status changes");
            Add("config_tracker_tone_pitch", "Tracker tone pitch");
            Add("config_tracker_tone_pitch_low", "Low");
            Add("config_tracker_tone_pitch_mid", "Mid");
            Add("config_tracker_tone_pitch_high", "High");
            Add("room_announcement", "Room: {0}");
            Add("nearby_announcement_without_prompt", "{0}");
            Add("nearby_announcement_closed_door", "{0}, closed.");
            Add("dateviators_equipped", "equipped");
            Add("dateviators_unequipped", "unequipped");
            Add("dateviators_state", "Dateviators {0}. {1} charges.");
            Add("time_announcement", "Time: {0}.");
            // Time change with the day of the week from the calendar. {0} = day, {1} = phase.
            Add("day_time_announcement", "{0}, {1}.");
            // Day names (calendar day of week). Keyed by the game's English DayOfWeek.ToString().
            Add("day_monday", "Monday");
            Add("day_tuesday", "Tuesday");
            Add("day_wednesday", "Wednesday");
            Add("day_thursday", "Thursday");
            Add("day_friday", "Friday");
            Add("day_saturday", "Saturday");
            Add("day_sunday", "Sunday");
            Add("collectable_unlocked", "Collectable unlocked. {0} total.");
            Add("dateable_added", "New dateable added to Date A Dex. {0} met.");
            Add("friend_ending_recorded", "Friend ending recorded. {0} total.");
            Add("love_ending_recorded", "Love ending recorded. {0} total.");
            Add("hate_ending_recorded", "Hate ending recorded. {0} total.");
            Add("realized_ending_recorded", "Realized ending recorded. {0} total.");
            Add("choice_announcement", "Choice {0} of {1}. {2}");
            Add("choice_locked_suffix", "{0}. Locked.");
            Add("choice_locked_activate", "This option is locked.");
            Add("apply_display_settings", "Apply display settings");
            Add("controls_unbound", "unbound");
            Add("new_game_field_name", "Name");
            Add("new_game_field_town", "Town");
            Add("new_game_field_favorite_thing", "Favorite thing");
            Add("new_game_field_pronouns", "Pronouns");
            Add("new_game_field_confirmation", "Confirmation");
            Add("new_game_field_empty", "Empty");
            Add("new_game_toggle_selected", "Selected");
            Add("new_game_toggle_not_selected", "Not selected");
            Add("new_game_pronoun_he_him", "He/Him");
            Add("new_game_pronoun_she_her", "She/Her");
            Add("new_game_pronoun_they_them", "They/Them");
            Add("phone_app_open_generic", "Phone app open.");
            Add("screen_open", "{0} open.");
            Add("roomers_character", "Character: {0}");
            Add("roomers_location", "Location: {0}");
            Add("canopy_no_messages", "Canopy. No active messages.");
            Add("music_no_track_selected", "No track selected");
            Add("music_playing", "Playing");
            Add("music_stopped", "Stopped");
            Add("music_detail", "Music. {0}. {1}.");
            Add("objective_announcement", "Objective. {0}");
            Add("loading_announcement", "Loading. {0}");
            Add("outcome_announcement", "Outcome. {0}");
            Add("phone_menu_summary", "Phone menu.");
            Add("dateadex_voice_actor", "Voice actor: {0}");
            Add("dateadex_likes", "Likes: {0}");
            Add("dateadex_dislikes", "Dislikes: {0}");
            Add("dateadex_pronouns", "Pronouns: {0}");
            Add("dateadex_collectables", "Collectables: {0}");
            Add("dateadex_unmet_description", "You haven't met this character yet.");
            Add("dateadex_button_collectables", "Collectables");
            Add("dateadex_button_collectables_value", "Collectables. {0}");
            Add("dateadex_collectable_unlocked", "{0}. {1}");
            Add("dateadex_collectable_locked", "Locked collectable. {0}");
            Add("dateadex_collectable_locked_plain", "Locked collectable");
            Add("dateadex_button_sort", "Sort");
            Add("dateadex_button_sort_value", "Sort. {0}");
            Add("dateadex_button_recipe", "Recipe");
            Add("dateadex_button_show_bio", "Show bio");
            Add("save_new_slot", "New save");
            Add("button_back", "Back");
            Add("button_save", "Save");
            Add("button_load", "Load");
            Add("button_delete", "Delete");
            Add("art_detail", "Art. {0}. {1}.");
            Add("specs_summary_stats", "SPECS. Stats.");
            Add("specs_summary_glossary", "SPECS. Glossary.");
            Add("specs_button_glossary", "Open glossary");
            Add("specs_button_stats", "Return to stats");
            Add("specs_button_profile", "Return to profile");
            Add("credits_summary", "Credits.");
            Add("language_english", "English");
            Add("language_japanese", "Japanese");
            Add("settings_summary", "Settings. Text language {0}. Master volume {1} percent. Music volume {2} percent.");
            Add("unknown_object", "object");
            Add("value_number", "Value {0}");
            Add("settings_slider_camera_sensitivity", "Camera sensitivity. Value {0}");
            Add("settings_slider_master_volume", "Master volume. Value {0}");
            Add("settings_slider_sfx_volume", "Sound effects volume. Value {0}");
            Add("settings_slider_music_volume", "Music volume. Value {0}");
            Add("settings_slider_voice_volume", "Voice volume. Value {0}");
            Add("settings_slider_field_of_view", "Field of view. Value {0}");
            Add("settings_slider_movement_speed", "Movement speed. Value {0}");
            Add("chat_app_only", "{0}");
            Add("chat_contact_only", "{0}. {1}");
            Add("chat_latest_message_without_contact", "{0}. Latest message. {1}");
            Add("chat_latest_message_with_contact", "{0}. {1}. Latest message. {2}");
            Add("chat_options", "Options. {0}");
            Add("navigation_no_objective", "No current objective.");
            Add("navigation_arrived", "Arrived at target.");
            Add("navigation_blocked", "Navigation blocked or interrupted.");
            Add("navigation_no_path", "No path found to {0}.");
            Add("navigation_tutorial_gift_delivery_trigger", "Gift delivery trigger");
            Add("navigation_planner_not_ready", "Navigation data not ready yet.");
            Add("navigation_object_picker_title", "Known objects");
            // Trailing position counter spoken AFTER the entry details: {0}=index, {1}=total.
            Add("navigation_object_picker_position", "{0} of {1}");
            Add("navigation_object_picker_empty", "No known objects available.");
            Add("navigation_object_picker_no_data", "Navigation data missing. The bake is incomplete.");
            Add("navigation_object_picker_empty_filtered", "No objects match the filters.");
            Add("navigation_object_picker_closed", "Object list closed.");
            // Section headers: {0}=count in section.
            Add("navigation_object_picker_section_met", "Met, {0}");
            Add("navigation_object_picker_section_encountered", "Encountered, {0}");
            // Met entry name: {0}=character, {1}=object.
            Add("navigation_object_picker_met_name", "{0}, {1}");
            // Floor tags.
            Add("navigation_object_picker_floor_named", "{0} floor");
            Add("navigation_object_picker_floor_other", "other floor");
            // Distance: {0}=metres.
            Add("navigation_object_picker_distance_m", "{0} metres");
            // Drill-in: a group's member count, spoken so the player knows to press Enter to open it.
            Add("navigation_object_picker_group_count", "{0} objects");
            // Room name when a target has no resolved zone.
            Add("navigation_object_picker_room_unknown", "unknown room");
            // Breadcrumb headers spoken when descending a level (ROOM-FIRST: rooms -> in-room -> objects).
            //   level_inroom: {0}=room — inside a room, choosing a datable or unmet object.
            //   level_objects: {0}=datable, {1}=room — a met datable's found objects in that room.
            Add("navigation_object_picker_level_inroom", "{0}");
            Add("navigation_object_picker_level_objects", "{0}, {1}");
            // Sort-mode announcements.
            Add("navigation_object_picker_sort_distance", "nearest first");
            Add("navigation_object_picker_sort_alpha", "alphabetical");
            // Floor-filter announcements.
            Add("navigation_object_picker_filter_floor_all", "all floors");
            Add("navigation_object_picker_filter_floor_current", "this floor only");
            // Section-filter announcements.
            Add("navigation_object_picker_filter_section_all", "all");
            Add("navigation_object_picker_filter_section_met", "met only");
            Add("navigation_object_picker_filter_section_encountered", "encountered only");
            // Doors-only announcements.
            Add("navigation_object_picker_filter_doors_on", "doors only");
            Add("navigation_object_picker_filter_doors_off", "all objects");
            Add("section_stepper_item", "{0} of {1}. {2}");
            Add("room_scan_title", "Room: {0}");
            Add("room_scan_empty", "Room: {0}. No trackable objects in this room.");
            Add("room_scan_unavailable", "Room report is not available right now.");
            Add("room_scan_unknown_room", "Unknown room");
            Add("room_scan_group", "{0}: {1}");
            Add("room_scan_direction_here", "Here");
            Add("room_scan_direction_ahead", "Ahead");
            Add("room_scan_direction_ahead_right", "Ahead right");
            Add("room_scan_direction_right", "Right");
            Add("room_scan_direction_behind_right", "Behind right");
            Add("room_scan_direction_behind", "Behind");
            Add("room_scan_direction_behind_left", "Behind left");
            Add("room_scan_direction_left", "Left");
            Add("room_scan_direction_ahead_left", "Ahead left");
            Add("navigation_target_in_current_room", "Current room");
            Add("navigation_autowalk_started", "Auto-walk started.");
            Add("navigation_autowalk_stopped", "Auto-walk stopped.");
            // Interaction feedback: state changes that only have visual feedback in the base
            // game (no distinct sound conveys the resulting state). See InteractionFeedbackPatches.
            // Thermostat temperature. Cold blows the vents; room temperature is the default.
            Add("thermostat_room_temp", "Room temperature.");
            Add("thermostat_cold", "Cold.");
            // Light switch result. {0} = the light's type/name (e.g. "Lights", "Lamp").
            // The switch click sounds identical either way, so the on/off state is visual-only.
            Add("light_on", "{0} on.");
            Add("light_off", "{0} off.");
            // Fallback name when a light switch has no configured type label.
            Add("light_default_name", "Light");
            // Dunk's sports-equipment datables. The base game labels most of these generically as
            // "sports equipment", but the storyline needs the player to click a SPECIFIC piece at a
            // specific time, so we speak the equipment category instead. See
            // ResolveDunkSportsEquipmentLabel in AccessibilityWatcher.
            Add("dunk_baseball", "Baseball");
            Add("dunk_football", "Football");
            Add("dunk_ball", "Ball");
            Add("dunk_kickball", "Kickball");
            Add("dunk_foam_block", "Foam block");
            Add("dunk_tennis_racket", "Tennis racket");
            Add("dunk_dumbbell", "Dumbbell");
            Add("dunk_kettlebell", "Kettlebell");
            Add("dunk_weight_plate", "Weight plate");
            Add("dunk_weight_rack", "Weight rack");
            Add("dunk_yoga_mat", "Yoga mat");
        }

        // Japanese overrides. Only entries whose Japanese differs from English are listed;
        // format-only templates ("{0}", "{0}: {1}", "{0}, {1}.") and proper nouns (SPECS, Canopy,
        // Credits, Dateviators, Date A Dex, He/Him ...) are intentionally omitted and fall through
        // to the English base. Placeholders {0..n} are preserved so string.Format still works.
        private static void InitializeJapanese()
        {
            AddJa("mod_loaded",
                "Date Everything Access を読み込みました。フォーカス項目、会話、画面テキスト、電話アプリのテキスト、部屋、近くのオブジェクト、状態の変化を自動で読み上げます。F1でヘルプ。バッククォート（`）で最後の読み上げを繰り返します。Lで現在の部屋と、向いている方向を基準にしたオブジェクトを知らせます。Ctrl+Oで現在の目標を追跡します。Oで既知オブジェクトの一覧を開きます。一覧では上下で選択を移動、Enterで決定、Backspaceで閉じる、左右で並び替え、Fでこの階のみ切り替え、Mでセクションフィルターを切り替え、Dでドアのみ切り替えます。Alt+Oで選択した目標へのオートウォークを切り替えます。Ctrl+F1でアクセシビリティ設定を開きます。");
            AddJa("help_text",
                "Date Everything Access。フォーカス項目、会話、画面テキスト、電話アプリのテキスト、部屋、近くのオブジェクト、状態の変化を自動で読み上げできます。F1でヘルプ。バッククォート（`）で最後の読み上げを繰り返します。Lで現在の部屋と、向いている方向を基準にしたオブジェクトを知らせます。Ctrl+Oで現在の目標を追跡します。Oで既知オブジェクトの一覧を開きます。一覧では上下で選択を移動、Enterで決定、Backspaceで閉じる、左右で並び替え、Fでこの階のみ切り替え、Mでセクションフィルターを切り替え、Dでドアのみ切り替えます。Alt+Oで選択した目標へのオートウォークを切り替えます。Ctrl+F1でアクセシビリティ設定を開きます。");
            AddJa("debug_mode_enabled", "デバッグモードを有効にしました。");
            AddJa("debug_mode_enabled_with_mapping_dump", "デバッグモードを有効にしました。{0} 台のデバイスの現在の入力割り当てをログに書き出しました。");
            AddJa("debug_mode_disabled", "デバッグモードを無効にしました。");
            AddJa("repeat_last_unavailable", "まだ何も読み上げていません。");
            AddJa("settings_menu_opened", "アクセシビリティ設定を開きました。");
            AddJa("settings_menu_closed", "アクセシビリティ設定を閉じて保存しました。");
            AddJa("settings_menu_item", "{1} 件中 {0} 件目: {2}、{3}。左右で値を変更します。EnterとSpaceも使えます。Backspaceで閉じます。");
            AddJa("settings_value_on", "オン");
            AddJa("settings_value_off", "オフ");
            AddJa("config_focused_items", "フォーカス項目");
            AddJa("config_dialogue_text", "会話テキスト");
            AddJa("config_dialogue_choices", "会話の選択肢");
            AddJa("config_screen_text", "画面テキスト");
            AddJa("config_phone_app_text", "電話アプリのテキスト");
            AddJa("config_room_changes", "部屋の移動");
            AddJa("config_nearby_objects", "近くのオブジェクト");
            AddJa("config_status_changes", "状態の変化");
            AddJa("config_tracker_tone_pitch", "トラッカー音の高さ");
            AddJa("config_tracker_tone_pitch_low", "低");
            AddJa("config_tracker_tone_pitch_mid", "中");
            AddJa("config_tracker_tone_pitch_high", "高");
            AddJa("room_announcement", "部屋: {0}");
            AddJa("nearby_announcement_closed_door", "{0}、閉まっています。");
            AddJa("dateviators_equipped", "装着");
            AddJa("dateviators_unequipped", "取り外し");
            AddJa("dateviators_state", "デートビュエーター{0}。チャージ {1}。");
            AddJa("time_announcement", "時刻: {0}。");
            AddJa("day_monday", "月曜日");
            AddJa("day_tuesday", "火曜日");
            AddJa("day_wednesday", "水曜日");
            AddJa("day_thursday", "木曜日");
            AddJa("day_friday", "金曜日");
            AddJa("day_saturday", "土曜日");
            AddJa("day_sunday", "日曜日");
            AddJa("collectable_unlocked", "コレクションを解除しました。合計 {0} 個。");
            AddJa("dateable_added", "Date A Dex に新しいデート相手を追加しました。{0} 人と対面済み。");
            AddJa("friend_ending_recorded", "友情エンドを記録しました。合計 {0} 個。");
            AddJa("love_ending_recorded", "恋愛エンドを記録しました。合計 {0} 個。");
            AddJa("hate_ending_recorded", "険悪エンドを記録しました。合計 {0} 個。");
            AddJa("realized_ending_recorded", "覚醒エンドを記録しました。合計 {0} 個。");
            AddJa("choice_announcement", "選択肢 {1} 件中 {0} 件目。{2}");
            AddJa("choice_locked_suffix", "{0}。ロック中。");
            AddJa("choice_locked_activate", "この選択肢はロックされています。");
            AddJa("apply_display_settings", "表示設定を適用");
            AddJa("controls_unbound", "未割り当て");
            AddJa("new_game_field_name", "名前");
            AddJa("new_game_field_town", "町");
            AddJa("new_game_field_favorite_thing", "好きなもの");
            AddJa("new_game_field_pronouns", "代名詞");
            AddJa("new_game_field_confirmation", "確認");
            AddJa("new_game_field_empty", "空");
            AddJa("new_game_toggle_selected", "選択中");
            AddJa("new_game_toggle_not_selected", "未選択");
            AddJa("phone_app_open_generic", "電話アプリを開きました。");
            AddJa("screen_open", "{0} を開きました。");
            AddJa("roomers_character", "キャラクター: {0}");
            AddJa("roomers_location", "場所: {0}");
            AddJa("canopy_no_messages", "Canopy。アクティブなメッセージはありません。");
            AddJa("music_no_track_selected", "トラックが選択されていません");
            AddJa("music_playing", "再生中");
            AddJa("music_stopped", "停止");
            AddJa("music_detail", "音楽。{0}。{1}。");
            AddJa("objective_announcement", "目標。{0}");
            AddJa("loading_announcement", "読み込み中。{0}");
            AddJa("outcome_announcement", "結果。{0}");
            AddJa("phone_menu_summary", "電話メニュー。");
            AddJa("dateadex_voice_actor", "声優: {0}");
            AddJa("dateadex_likes", "好き: {0}");
            AddJa("dateadex_dislikes", "嫌い: {0}");
            AddJa("dateadex_pronouns", "代名詞: {0}");
            AddJa("dateadex_collectables", "コレクション: {0}");
            AddJa("dateadex_unmet_description", "このキャラクターとはまだ対面していません。");
            AddJa("dateadex_button_collectables", "コレクション");
            AddJa("dateadex_button_collectables_value", "コレクション。{0}");
            AddJa("dateadex_collectable_locked", "ロック中のコレクション。{0}");
            AddJa("dateadex_collectable_locked_plain", "ロック中のコレクション");
            AddJa("dateadex_button_sort", "並び替え");
            AddJa("dateadex_button_sort_value", "並び替え。{0}");
            AddJa("dateadex_button_recipe", "レシピ");
            AddJa("dateadex_button_show_bio", "プロフィールを表示");
            AddJa("save_new_slot", "新規セーブ");
            AddJa("button_back", "戻る");
            AddJa("button_save", "セーブ");
            AddJa("button_load", "ロード");
            AddJa("button_delete", "削除");
            AddJa("art_detail", "アート。{0}。{1}。");
            AddJa("specs_summary_stats", "SPECS。ステータス。");
            AddJa("specs_summary_glossary", "SPECS。用語集。");
            AddJa("specs_button_glossary", "用語集を開く");
            AddJa("specs_button_stats", "ステータスに戻る");
            AddJa("specs_button_profile", "プロフィールに戻る");
            AddJa("language_english", "英語");
            AddJa("language_japanese", "日本語");
            AddJa("settings_summary", "設定。テキスト言語 {0}。マスター音量 {1} パーセント。音楽音量 {2} パーセント。");
            AddJa("unknown_object", "オブジェクト");
            AddJa("value_number", "値 {0}");
            AddJa("settings_slider_camera_sensitivity", "カメラ感度。値 {0}");
            AddJa("settings_slider_master_volume", "マスター音量。値 {0}");
            AddJa("settings_slider_sfx_volume", "効果音の音量。値 {0}");
            AddJa("settings_slider_music_volume", "音楽の音量。値 {0}");
            AddJa("settings_slider_voice_volume", "ボイスの音量。値 {0}");
            AddJa("settings_slider_field_of_view", "視野角。値 {0}");
            AddJa("settings_slider_movement_speed", "移動速度。値 {0}");
            AddJa("chat_latest_message_without_contact", "{0}。最新のメッセージ。{1}");
            AddJa("chat_latest_message_with_contact", "{0}。{1}。最新のメッセージ。{2}");
            AddJa("chat_options", "オプション。{0}");
            AddJa("navigation_no_objective", "現在の目標はありません。");
            AddJa("navigation_arrived", "目標に到着しました。");
            AddJa("navigation_blocked", "ナビゲーションが妨げられたか中断されました。");
            AddJa("navigation_no_path", "{0} への経路が見つかりません。");
            AddJa("navigation_tutorial_gift_delivery_trigger", "ギフト配達トリガー");
            AddJa("navigation_planner_not_ready", "ナビゲーションデータの準備がまだできていません。");
            AddJa("navigation_object_picker_title", "既知のオブジェクト");
            AddJa("navigation_object_picker_position", "{1} 件中 {0} 件目");
            AddJa("navigation_object_picker_empty", "利用できる既知オブジェクトはありません。");
            AddJa("navigation_object_picker_no_data", "ナビゲーションデータがありません。ベイクが不完全です。");
            AddJa("navigation_object_picker_empty_filtered", "フィルターに一致するオブジェクトはありません。");
            AddJa("navigation_object_picker_closed", "オブジェクト一覧を閉じました。");
            AddJa("navigation_object_picker_section_met", "対面済み、{0}");
            AddJa("navigation_object_picker_section_encountered", "発見済み、{0}");
            AddJa("navigation_object_picker_floor_named", "{0} 階");
            AddJa("navigation_object_picker_floor_other", "別の階");
            AddJa("navigation_object_picker_distance_m", "{0} メートル");
            AddJa("navigation_object_picker_group_count", "{0} 個のオブジェクト");
            AddJa("navigation_object_picker_room_unknown", "不明な部屋");
            AddJa("navigation_object_picker_sort_distance", "近い順");
            AddJa("navigation_object_picker_sort_alpha", "五十音順");
            AddJa("navigation_object_picker_filter_floor_all", "すべての階");
            AddJa("navigation_object_picker_filter_floor_current", "この階のみ");
            AddJa("navigation_object_picker_filter_section_all", "すべて");
            AddJa("navigation_object_picker_filter_section_met", "対面済みのみ");
            AddJa("navigation_object_picker_filter_section_encountered", "発見済みのみ");
            AddJa("navigation_object_picker_filter_doors_on", "ドアのみ");
            AddJa("navigation_object_picker_filter_doors_off", "すべてのオブジェクト");
            AddJa("section_stepper_item", "{1} 件中 {0} 件目。{2}");
            AddJa("room_scan_title", "部屋: {0}");
            AddJa("room_scan_empty", "部屋: {0}。この部屋に追跡可能なオブジェクトはありません。");
            AddJa("room_scan_unavailable", "現在、部屋レポートは利用できません。");
            AddJa("room_scan_unknown_room", "不明な部屋");
            AddJa("room_scan_direction_here", "ここ");
            AddJa("room_scan_direction_ahead", "前");
            AddJa("room_scan_direction_ahead_right", "右前");
            AddJa("room_scan_direction_right", "右");
            AddJa("room_scan_direction_behind_right", "右後ろ");
            AddJa("room_scan_direction_behind", "後ろ");
            AddJa("room_scan_direction_behind_left", "左後ろ");
            AddJa("room_scan_direction_left", "左");
            AddJa("room_scan_direction_ahead_left", "左前");
            AddJa("navigation_target_in_current_room", "現在の部屋");
            AddJa("navigation_autowalk_started", "オートウォークを開始しました。");
            AddJa("navigation_autowalk_stopped", "オートウォークを停止しました。");
            AddJa("thermostat_room_temp", "常温。");
            AddJa("thermostat_cold", "冷房。");
            AddJa("light_on", "{0} オン。");
            AddJa("light_off", "{0} オフ。");
            AddJa("light_default_name", "ライト");
            AddJa("dunk_baseball", "野球ボール");
            AddJa("dunk_football", "フットボール");
            AddJa("dunk_ball", "ボール");
            AddJa("dunk_kickball", "キックボール");
            AddJa("dunk_foam_block", "フォームブロック");
            AddJa("dunk_tennis_racket", "テニスラケット");
            AddJa("dunk_dumbbell", "ダンベル");
            AddJa("dunk_kettlebell", "ケトルベル");
            AddJa("dunk_weight_plate", "ウェイトプレート");
            AddJa("dunk_weight_rack", "ウェイトラック");
            AddJa("dunk_yoga_mat", "ヨガマット");
        }
    }
}
