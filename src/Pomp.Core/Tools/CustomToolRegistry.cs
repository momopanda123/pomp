using System;
using System.Collections.Generic;

namespace Pomp.Tools
{
    /// <summary>
    /// Thread-safe catalogue of host tools (design: CustomToolRegistry). Names are case-sensitive, exactly as omp keys
    /// them; registering a name twice is an error. Definitions are handed to omp in registration order.
    /// </summary>
    public sealed class CustomToolRegistry : ICustomToolRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, IHostTool> _tools = new Dictionary<string, IHostTool>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        /// <inheritdoc />
        public void Register(IHostTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            var definition = tool.Definition ?? throw new ArgumentException("The host tool has no definition.", nameof(tool));
            var name = definition.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A host tool needs a name.", nameof(tool));
            }

            if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
            {
                // omp trims names when it registers them; a padded key would never match the host_tool_call name.
                throw new ArgumentException("Host tool name '" + name + "' has leading or trailing whitespace.", nameof(tool));
            }

            lock (_lock)
            {
                if (_tools.ContainsKey(name))
                {
                    throw new ArgumentException("A host tool named '" + name + "' is already registered.", nameof(tool));
                }

                _tools.Add(name, tool);
                _order.Add(name);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<HostToolDefinition> GetDefinitions()
        {
            lock (_lock)
            {
                var definitions = new List<HostToolDefinition>(_order.Count);
                foreach (var name in _order)
                {
                    definitions.Add(_tools[name].Definition);
                }

                return definitions;
            }
        }

        /// <inheritdoc />
        public bool TryGet(string toolName, out IHostTool tool)
        {
            if (toolName != null)
            {
                lock (_lock)
                {
                    if (_tools.TryGetValue(toolName, out var found))
                    {
                        tool = found;
                        return true;
                    }
                }
            }

            tool = null!;
            return false;
        }
    }
}
