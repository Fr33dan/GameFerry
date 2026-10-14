using System.Globalization;
using System.Text;

namespace GameFerry.NKitHost
{
    /// <summary>Builds one flat JSON object, without pulling a serializer into the host.</summary>
    internal sealed class Json
    {
        private readonly StringBuilder _text = new StringBuilder("{");

        public Json Add(string name, string value)
        {
            Name(name);
            if (value == null)
                _text.Append("null");
            else
                Quote(value);
            return this;
        }

        public Json Add(string name, bool value)
        {
            Name(name);
            _text.Append(value ? "true" : "false");
            return this;
        }

        public Json Add(string name, long value)
        {
            Name(name);
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Json Add(string name, double value)
        {
            Name(name);
            bool finite = !double.IsNaN(value) && !double.IsInfinity(value);
            _text.Append(finite ? value.ToString("R", CultureInfo.InvariantCulture) : "0");
            return this;
        }

        public override string ToString()
        {
            return _text + "}";
        }

        private void Name(string name)
        {
            if (_text.Length > 1)
                _text.Append(',');
            Quote(name);
            _text.Append(':');
        }

        private void Quote(string value)
        {
            _text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        _text.Append("\\\"");
                        break;
                    case '\\':
                        _text.Append("\\\\");
                        break;
                    case '\n':
                        _text.Append("\\n");
                        break;
                    case '\r':
                        _text.Append("\\r");
                        break;
                    case '\t':
                        _text.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                            _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _text.Append(c);
                        break;
                }
            }

            _text.Append('"');
        }
    }
}
