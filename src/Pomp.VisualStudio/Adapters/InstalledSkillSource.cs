using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Features;
using Microsoft.VisualStudio.Threading;
using Task=System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Discovers only already installed MEF exports, on demand. Providers must be thread-safe, metadata-only and fast.</summary>
    internal sealed class InstalledSkillSource:IInstalledSkillSource
    {
        private readonly VsAdapterContext context;
        public InstalledSkillSource(VsAdapterContext context){this.context=context;}
        public async Task<IReadOnlyList<InstalledSkillPackage>> DiscoverAsync(CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            var exports=context.ComponentModel.DefaultExportProvider.GetExports<IPompSkillProvider>().Take(51).ToArray();
            await TaskScheduler.Default;
            var packages=new List<InstalledSkillPackage>();
            foreach(var export in exports.Take(50))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var provider=export.Value;var assembly=provider.GetType().Assembly;var directory=Path.GetDirectoryName(assembly.Location);
                    if(string.IsNullOrEmpty(directory))throw new InvalidOperationException("Provider has no installed assembly location.");
                    var metadata=provider.GetSkills().Take(201).Select(s=>new PompSkillDefinition{Id=s.Id,Description=s.Description,RelativePath=s.RelativePath}).ToArray();
                    packages.Add(new InstalledSkillPackage(assembly.GetName().Name??"",directory!,metadata));
                }
                catch(Exception ex){context.LogUnexpected("Installed skill provider",ex);packages.Add(new InstalledSkillPackage("invalid","",Array.Empty<PompSkillDefinition>(),"An installed skill provider failed; other providers remain available."));}
            }
            if(exports.Length>50)packages.Add(new InstalledSkillPackage("limited","",Array.Empty<PompSkillDefinition>(),"Installed skill discovery limited to 50 providers."));
            return packages.AsReadOnly();
        }
    }
}
