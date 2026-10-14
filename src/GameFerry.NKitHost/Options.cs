using System;
using System.Collections.Generic;

namespace GameFerry.NKitHost
{
    /// <summary>Command-line options of the form <c>--name value</c>, plus bare <c>--flag</c> switches.</summary>
    internal sealed class Options
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            Options options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Unexpected argument '" + args[i] + "'.");

                string name = args[i].Substring(2);
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                options._values[name] = hasValue ? args[++i] : null;
            }

            return options;
        }

        public bool Has(string name)
        {
            return _values.ContainsKey(name);
        }

        public string Get(string name)
        {
            string value;
            return _values.TryGetValue(name, out value) ? value : null;
        }

        public string Require(string name)
        {
            string value = Get(name);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("Missing required option --" + name + ".");
            return value;
        }
    }
}
