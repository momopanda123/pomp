using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Pomp.Features;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Adapters
{
    internal sealed class ProjectEventSource : IProjectEventSource, IDisposable
    {
        private readonly VsAdapterContext context;
        private DocumentEvents? documents;
        private BuildEvents? builds;
        private string buildRoot = "";
        private bool disposed;
        public ProjectEventSource(VsAdapterContext context) { this.context = context; }
        public event EventHandler<ProjectEvent>? Occurred;
        public async Task InitializeAsync(CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            try { documents=context.Dte.Events.DocumentEvents; builds=context.Dte.Events.BuildEvents; documents.DocumentSaved+=OnSaved; builds.OnBuildBegin+=OnBuildBegin; builds.OnBuildDone+=OnBuildDone; }
            catch(Exception ex){context.LogUnexpected("Subscribe project hook events",ex);}
        }
        private string Root() { ThreadHelper.ThrowIfNotOnUIThread(); return SolutionLocation.Resolve(context.Solution)?.RootDirectory ?? ""; }
        private void OnSaved(Document document)
        {
            if(disposed)return;
            try {ThreadHelper.ThrowIfNotOnUIThread();var root=Root();if(root.Length>0)Occurred?.Invoke(this,new ProjectEvent("onFileSave",root,document.FullName));}
            catch(Exception ex){context.LogUnexpected("Project saved-file hook event",ex);}
        }
        private void OnBuildBegin(vsBuildScope scope,vsBuildAction action){if(disposed)return;try{ThreadHelper.ThrowIfNotOnUIThread();buildRoot=Root();}catch(Exception ex){buildRoot="";context.LogUnexpected("Project build hook start",ex);}}
        private void OnBuildDone(vsBuildScope scope,vsBuildAction action)
        {
            if(disposed)return;var root=buildRoot;buildRoot="";
            try
            {
                if(root.Length==0)return;
                ThreadHelper.ThrowIfNotOnUIThread();
                // Routine rules need the outcome, which the callback args do not carry: like BuildAdapter, DTE owns it via
                // LastBuildInfo (number of failed projects). Detail stays the action name for .pomp/hooks; a Clean has no outcome.
                bool? succeeded=null;
                if(action!=vsBuildAction.vsBuildActionClean)
                {
                    try
                    {
                        var failed=context.Dte.Solution?.SolutionBuild?.LastBuildInfo ?? -1;
                        if(failed>=0)succeeded=failed==0;
                    }
                    catch(Exception ex){context.LogUnexpected("Project build hook outcome",ex);}
                }
                Occurred?.Invoke(this,new ProjectEvent("onBuild",root,"",action.ToString(),succeeded));
            }
            catch(Exception ex){context.LogUnexpected("Project build hook completion",ex);}
        }
        public void Dispose()
        {
            disposed=true;
            _=context.Jtf.RunAsync(async()=>{await context.Jtf.SwitchToMainThreadAsync();try{if(documents!=null)documents.DocumentSaved-=OnSaved;if(builds!=null){builds.OnBuildBegin-=OnBuildBegin;builds.OnBuildDone-=OnBuildDone;}}catch(Exception){ /* IDE shutdown */ }});
        }
    }
}
