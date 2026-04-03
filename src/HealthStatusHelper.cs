namespace DocumentHealth
{
    /// <summary>
    /// Helper class containing testable logic extracted from various UI classes.
    /// </summary>
    internal static class HealthStatusHelper
    {
        private const string NoIssuesText = "No errors or warnings";
        private const string ErrorsText = "{0} error(s)";
        private const string WarningsText = "{0} warning(s)";
        private const string MessagesText = "{0} message(s)";

        /// <summary>
        /// Generates accessible text describing the diagnostic counts.
        /// </summary>
        internal static string GetAccessibleText(int errors, int warnings, int messages)
        {
            if (errors == 0 && warnings == 0 && messages == 0)
            {
                return NoIssuesText;
            }

            var parts = new System.Collections.Generic.List<string>(3);
            if (errors > 0)
            {
                parts.Add(string.Format(ErrorsText, errors));
            }
            if (warnings > 0)
            {
                parts.Add(string.Format(WarningsText, warnings));
            }
            if (messages > 0)
            {
                parts.Add(string.Format(MessagesText, messages));
            }
            return string.Join(", ", parts);
        }
    }
}
