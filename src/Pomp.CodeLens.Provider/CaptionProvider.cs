using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Pomp.CodeLens.Contracts;

namespace Pomp.CodeLens.Provider
{
    /// <summary>Runs in the CodeLens service. No file reads, provider calls or process launches.</summary>
    [Export(typeof(IAsyncCodeLensDataPointProvider))]
    [Name("POMP AI summaries")]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [Priority(210)]
    public sealed class CaptionProvider : IAsyncCodeLensDataPointProvider
    {
        private readonly ICodeLensCallbackService callbacks;
        [ImportingConstructor]
        public CaptionProvider(ICodeLensCallbackService callbacks) => this.callbacks = callbacks;
        public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(descriptor != null && !string.IsNullOrWhiteSpace(descriptor.FilePath) && descriptor.FilePath.Length <= 32767);
        }
        public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncCodeLensDataPoint>(new Point(descriptor, callbacks));
        }
        private sealed class Point : IAsyncCodeLensDataPoint
        {
            private readonly ICodeLensCallbackService callbacks;
            public Point(CodeLensDescriptor descriptor, ICodeLensCallbackService callbacks) { Descriptor = descriptor; this.callbacks = callbacks; }
            public CodeLensDescriptor Descriptor { get; }
            // The label is static. VS supplies fresh descriptor context when opening the details.
            public event AsyncEventHandler InvalidatedAsync { add { } remove { } }
            public async Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext context, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    if (!await callbacks.InvokeAsync<bool>(this, "PompCodeLens.IsEnabled", Array.Empty<object>(), timeout.Token).ConfigureAwait(false)) return null!;
                    var tooltip = await callbacks.InvokeAsync<string>(this, "PompCodeLens.Tooltip", Array.Empty<object>(), timeout.Token).ConfigureAwait(false);
                    return new CodeLensDataPointDescriptor { Description = "POMP AI", TooltipText = tooltip };
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null!; }
                catch (OperationCanceledException) { throw; }
                catch { return null!; } // Unavailable host means no indicator, never a fallback inference.
            }
            public Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext context, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!context.ApplicableSpan.HasValue) return Task.FromResult(new CodeLensDetailsDescriptor());
                return Task.FromResult(new CodeLensDetailsDescriptor { CustomData = new List<CaptionDetail> { new CaptionDetail { Path = Descriptor.FilePath, Start = context.ApplicableSpan.Value.Start, Length = context.ApplicableSpan.Value.Length } } });
            }
        }
    }
}
