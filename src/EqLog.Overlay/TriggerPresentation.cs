using EqLog.Core.Triggers;

namespace EqLog.Overlay;

internal sealed record TriggerText(string Text, ArgumentException? Error = null);

internal static class TriggerPresentation
{
    internal static string CompileError(TriggerCompileError error)
    {
        return $"{(error.Name.Length > 0 ? error.Name : error.Pattern)}: {error.Diagnostic}";
    }

    internal static TriggerText Format(TriggerAlert alert)
    {
        if (string.IsNullOrWhiteSpace(alert.Trigger.Text))
        {
            return new(alert.Message);
        }
        try
        {
            return new(alert.Captures.Result(alert.Trigger.Text).Replace("{C}", alert.CharacterName, StringComparison.Ordinal));
        }
        catch (ArgumentException error)
        {
            return new(alert.Trigger.Text.Replace("{C}", alert.CharacterName, StringComparison.Ordinal), error);
        }
    }
}
