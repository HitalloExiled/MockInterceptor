using System.Text;

namespace MockInterceptor.Generator;

public class CodeWriter
{
    private readonly StringBuilder source = new();
    private int identation;

    private void WriteIdent()
    {
        this.source.EnsureCapacity(this.source.Length + this.identation);

        for (var i = 0; i < this.identation; i++)
        {
            this.source.Append(' ');
        }
    }

    public void Deindent() => this.identation -= 4;
    public void Indent() => this.identation += 4;

    public void Write(string value)
    {
        this.WriteIdent();
        this.source.Append(value);
    }

    public void WriteLine(string? value = null)
    {
        if (value != null)
        {
            this.WriteIdent();
        }

        this.source.AppendLine(value);
    }

    public override string ToString() =>
        this.source.ToString();
}
