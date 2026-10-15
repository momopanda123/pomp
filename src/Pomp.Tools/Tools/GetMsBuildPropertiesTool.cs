using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Pomp.Tools
{
    /// <summary>Read-only evaluated MSBuild properties exposed by the loaded project system.</summary>
    internal sealed class GetMsBuildPropertiesTool : HostToolBase
    {
        internal const int MaxProperties = 50;
        private static readonly string[] DefaultProperties =
        {
            "TargetFramework", "TargetFrameworks", "LangVersion", "Nullable", "ImplicitUsings", "OutputType", "AssemblyName", "RootNamespace",
            "Configuration", "Platform", "OutputPath", "DefineConstants", "TreatWarningsAsErrors", "Deterministic",
        };

        public GetMsBuildPropertiesTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.GetMsBuildProperties,
                Label = "Get MSBuild Properties",
                Description = "Read evaluated MSBuild properties exposed by one loaded project's Visual Studio project system. Read-only: it does not evaluate a project on disk or run MSBuild. " +
                    "Use a unique project name or full project file path; unknown or ambiguous projects are errors. " +
                    "Optional properties selects up to 50 MSBuild identifiers; omitted, null or empty uses the standard framework, language, output, configuration and compiler property set. " +
                    "Output is one 'Name = value' line per property; missing or empty values are '(not set)'. Long values are bounded and marked when shortened.",
                Parameters = new SchemaBuilder()
                    .String("project", "Project name or full project path.", required: true)
                    .StringArray("properties", "Property names to read (default: a standard set).", MaxProperties)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var arguments = new ToolArguments(invocation.Arguments, "project", "properties");
            var project = arguments.RequiredString("project");
            var requested = arguments.OptionalStringArray("properties", MaxProperties) ?? DefaultProperties;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in requested)
            {
                if (!IsIdentifier(name))
                {
                    arguments.AddProblem("'properties' contains an invalid MSBuild identifier '" + TextUtil.ToSingleLine(name, 100) + "' (use letters, digits and underscores; start with a letter or underscore; maximum 128 characters)");
                }
                else if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            if (arguments.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var values = await Adapters.Solution.GetMsBuildPropertiesAsync(project, names, cancellationToken).ConfigureAwait(false);
            if (values == null)
            {
                return Fail("The project adapter returned no MSBuild properties.");
            }

            var output = new StringBuilder();
            var details = new JObject();
            foreach (var name in names)
            {
                values.TryGetValue(name, out var value);
                if (output.Length > 0)
                {
                    output.Append('\n');
                }

                output.Append(name).Append(" = ").Append(string.IsNullOrEmpty(value) ? "(not set)" : TextUtil.ToSingleLine(value, 3000));
                details[name] = value == null ? JValue.CreateNull() : new JValue(TextUtil.ToSingleLine(value, 3000));
            }

            return HostToolResult.Text(output.ToString(), new JObject { ["project"] = project, ["properties"] = details });
        }

        private static bool IsIdentifier(string name)
        {
            if (name.Length == 0 || name.Length > 128 || !IsStart(name[0]))
            {
                return false;
            }

            for (var index = 1; index < name.Length; index++)
            {
                if (!IsStart(name[index]) && (name[index] < '0' || name[index] > '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsStart(char character) => character == '_' || (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');
    }
}
