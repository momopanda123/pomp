using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Typed reads of <see cref="IVsHierarchy"/> properties. UI thread only.</summary>
    internal static class HierarchyHelper
    {
        public static object? GetProperty(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return ErrorHandler.Succeeded(hierarchy.GetProperty(itemId, (int)property, out object value)) ? value : null;
        }

        public static string? GetString(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return GetProperty(hierarchy, itemId, property) as string;
        }

        public static Guid? GetGuid(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return ErrorHandler.Succeeded(hierarchy.GetGuidProperty(itemId, (int)property, out var guid)) ? guid : (Guid?)null;
        }

        /// <summary>Item id stored in a property such as FirstVisibleChild; <see cref="VSConstants.VSITEMID_NIL"/> when absent.</summary>
        public static uint GetItemId(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var value = GetProperty(hierarchy, itemId, property);
            switch (value)
            {
                case int signed:
                    return unchecked((uint)signed);
                case uint unsigned:
                    return unsigned;
                case long wide:
                    return unchecked((uint)wide);
                default:
                    return VSConstants.VSITEMID_NIL;
            }
        }

        /// <summary>Full path/moniker of an item through <see cref="IVsProject"/>, or null.</summary>
        public static string? GetMkDocument(IVsHierarchy hierarchy, uint itemId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (hierarchy is IVsProject project
                && ErrorHandler.Succeeded(project.GetMkDocument(itemId, out var path))
                && !string.IsNullOrEmpty(path))
            {
                return path;
            }

            return null;
        }
    }
}
