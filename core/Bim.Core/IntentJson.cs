namespace Bim.Core;

// P3-01 stub: public surface only; behaviour lands after the red CI run.
public static class IntentJson
{
    public const int MaxInputChars = 1048576;
    public const int MaxDepth = 8;

    public static LayoutIntent Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        throw new IntentException(IntentCodes.JsonInvalid);
    }

    public static string Serialize(LayoutIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return "{}";
    }
}
