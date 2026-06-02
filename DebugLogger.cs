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

        private static string GetPrefix(LogCategory category)
        {
            switch (category)
            {
                case LogCategory.State:
                    return "[STATE]";
                case LogCategory.Handler:
                    return "[HANDLER]";
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
        State,
        Handler
    }
}
