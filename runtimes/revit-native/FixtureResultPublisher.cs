using System.Diagnostics;
using System.Text.Json;
namespace Bim.Revit;
internal static class FixtureResultPublisher
{
    internal static void Publish(string path, object value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: false);
        } finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) { Trace.TraceError("BIM fixture temporary result cleanup failed: {0}", error); }
        }
    }
    internal static bool TryPublish(string path, object value)
    {
        try { Publish(path, value); return true; }
        catch (Exception error) { Trace.TraceError("BIM fixture result publication failed: {0}", error); return false; }
    }
}

