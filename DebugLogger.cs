namespace DateEverythingAccess
{
    /// <summary>
    /// Centralized debug logging helpers.
    /// </summary>
    public static class DebugLogger
    {
        public static void Log(LogCategory category, string message)
        {
            if (!Main.DebugMode)
                return;

            string prefix = GetPrefix(category);
            Main.Log.LogInfo(prefix + " " + message);
        }

        public static void Log(LogCategory category, string source, string message)
        {
            if (!Main.DebugMode)
                return;

            string prefix = GetPrefix(category);
            Main.Log.LogInfo(prefix + " [" + source + "] " + message);
        }

        public static void LogScreenReader(string text)
        {
            if (!Main.DebugMode)
                return;

            Main.Log.LogInfo("[SR] " + text);
        }

        public static void LogInput(string keyName, string action = null)
        {
            if (!Main.DebugMode)
                return;

            string msg = action != null ? keyName + " -> " + action : keyName;
            Main.Log.LogInfo("[INPUT] " + msg);
        }

        public static void LogState(string description)
        {
            if (!Main.DebugMode)
                return;

            Main.Log.LogInfo("[STATE] " + description);
        }

        private static string GetPrefix(LogCategory category)
        {
            switch (category)
            {
                case LogCategory.ScreenReader:
                    return "[SR]";
                case LogCategory.Input:
                    return "[INPUT]";
                case LogCategory.State:
                    return "[STATE]";
                case LogCategory.Handler:
                    return "[HANDLER]";
                case LogCategory.Game:
                    return "[GAME]";
                default:
                    return "[DEBUG]";
            }
        }
    }

    /// <summary>
    /// Debug log categories.
    /// </summary>
    public enum LogCategory
    {
        ScreenReader,
        Input,
        State,
        Handler,
        Game
    }
}
