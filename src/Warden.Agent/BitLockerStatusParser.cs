namespace Warden.Agent;

public static class BitLockerStatusParser
{
    public static bool IsProtectionEnabled(string manageBdeStatusOutput)
    {
        foreach (var rawLine in manageBdeStatusOutput.Split(
                     new[] { "\r\n", "\n" },
                     StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Protection Status:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Protection Status:".Length..].Trim();
            if (value.Equals("Protection On", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("Protection Off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new InvalidOperationException($"Unrecognized BitLocker protection status: '{value}'");
        }

        throw new InvalidOperationException("manage-bde output did not include a Protection Status line.");
    }
}
