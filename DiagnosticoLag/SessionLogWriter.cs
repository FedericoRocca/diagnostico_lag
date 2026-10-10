using System.Text;

namespace DiagnosticoLag;

internal sealed class SessionLogWriter
{
    public void AppendText(string path, string text) =>
        File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));

    public void AppendCsv(string path, string text) =>
        File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(true));
}
