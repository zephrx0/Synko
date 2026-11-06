using System.Runtime.InteropServices;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal static class BrowserAddressReader
{
    internal sealed record Result(MediaWebsite Website, string? VideoId, string? Diagnostic = null);
    internal static string? LastDiagnostic { get; private set; }
    private static readonly string[] AddressBarNames =
    [
        "Address and search bar", "Адресная строка и строка поиска", "Адресная строка",
        "Search or enter address", "Поиск или ввод адреса", "Search with Google or enter address",
        "Search or enter URL", "Search or type URL", "Search or enter web address", "Введите адрес или поисковый запрос"
    ];

    public static Result Read(IntPtr window, string mediaTitle, bool probeAddress = false)
    {
        var activeYouTube = BrowserSiteClassifier.FromWindowTitle(mediaTitle, BrowserWebsiteDetector.WindowTitle(window)) == MediaWebsite.YouTube;
        LastDiagnostic = "Reading browser navigation; activeYouTube=" + activeYouTube;
        var references = new List<object>();
        try
        {
            var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("ff48dba4-60ef-4201-aa87-54103eef594e"), true)!)!;
            references.Add(automation);
            automation.ElementFromHandle(window, out var root); references.Add(root);
            var navigation = ReadNavigation(automation, root, references);
            foreach (var node in navigation.Where(n => n.Type == 50030))
            {
                if (!MatchesMediaTitle(mediaTitle, Name(node.Element))) continue;
                if (VideoId(node.Element) is { } documentId && MatchesMediaTitle(mediaTitle, Name(node.Element)))
                { LastDiagnostic = "Matched YouTube document title and URL"; return new(MediaWebsite.YouTube, documentId); }
            }
            if (!activeYouTube)
            {
                foreach (var node in navigation.Where(n => n.Type == 50019))
                {
                    var tab = node.Element;
                    if (BrowserSiteClassifier.FromWindowTitle(mediaTitle, Name(tab)) != MediaWebsite.YouTube) continue;
                    if (probeAddress)
                    {
                        var original = navigation.FirstOrDefault(n => n.Type == 50019 && Selected(n.Element));
                        if (original is not null)
                        {
                            original.Element.GetCurrentPattern(10010, out var originalSelection); references.Add(originalSelection);
                            tab.GetCurrentPattern(10010, out var selection); references.Add(selection);
                            try { selection.Select(); Thread.Sleep(250); return Read(window, mediaTitle); }
                            finally { originalSelection.Select(); LastDiagnostic = "Probed media address and restored the original tab"; }
                        }
                    }
                    if (VideoId(tab, includeDescriptions: true) is { } id)
                    { LastDiagnostic = "Matched background YouTube tab and URL"; return new(MediaWebsite.YouTube, id); }
                    LastDiagnostic = "YouTube tab matched; browser does not expose its public URL";
                    return new(MediaWebsite.YouTube, null);
                }
                return new(MediaWebsite.Unknown, null);
            }
            foreach (var node in navigation.Where(n => n.Type is 50003 or 50004))
            {
                var address = node.Element;
                address.GetCurrentPropertyValue(30011, out var automationId);
                var name = Name(address);
                var named = AddressBarNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || automationId is "urlbar" or "urlbar-input" or "omnibox"
                    || (name.StartsWith("Search with ", StringComparison.OrdinalIgnoreCase) && name.EndsWith(" or enter address", StringComparison.OrdinalIgnoreCase));
                if (!named && !node.InToolbar) continue;
                address.GetCurrentPropertyValue(30008, out var focused);
                if (focused is true) continue;
                if (VideoId(address) is { } id && BrowserSiteClassifier.FromWindowTitle(mediaTitle, BrowserWebsiteDetector.WindowTitle(window)) == MediaWebsite.YouTube)
                { LastDiagnostic = "Matched browser address URL"; return new(MediaWebsite.YouTube, id); }
            }
            LastDiagnostic = "Playing YouTube matched; public video URL unavailable";
            return new(MediaWebsite.YouTube, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { LastDiagnostic = ex.GetType().Name + ": " + ex.Message; return new(activeYouTube ? MediaWebsite.YouTube : MediaWebsite.Unknown, null); }
        finally
        {
            foreach (var reference in references.Distinct(ReferenceEqualityComparer.Instance).Reverse())
                if (Marshal.IsComObject(reference))
                    try { Marshal.ReleaseComObject(reference); } catch (InvalidComObjectException) { }
        }
    }

    private sealed record NavigationNode(IUIAutomationElement Element, int Type, bool InToolbar);
    private static List<NavigationNode> ReadNavigation(IUIAutomation automation, IUIAutomationElement root, List<object> references)
    {
        // Do not run FindAll(Descendants) over a video's entire DOM. Enumerate
        // browser navigation and document roots, never the document contents.
        automation.CreateTrueCondition(out var all); references.Add(all);
        var nodes = new List<NavigationNode>();
        var pending = new Queue<(IUIAutomationElement Element, int Depth, bool Toolbar)>();
        pending.Enqueue((root, 0, false));
        while (pending.Count > 0 && nodes.Count < 384)
        {
            var current = pending.Dequeue();
            try
            {
                current.Element.GetCurrentPropertyValue(30003, out var kind);
                var type = kind is int value ? value : 0;
                var toolbar = current.Toolbar || type == 50021;
                nodes.Add(new(current.Element, type, toolbar));
                if (type is 50030 or 50019 || current.Depth >= 12) continue;
                current.Element.FindAll(2, all, out var children); references.Add(children);
                children.GetLength(out var count);
                for (var i = 0; i < count && nodes.Count + pending.Count < 384; i++)
                {
                    children.GetElement(i, out var child); references.Add(child);
                    pending.Enqueue((child, current.Depth + 1, toolbar));
                }
            }
            catch (COMException) { /* A browser control may disappear during navigation. */ }
        }
        return nodes;
    }

    private static bool MatchesMediaTitle(string mediaTitle, string title) => !string.IsNullOrWhiteSpace(mediaTitle)
        && (BrowserSiteClassifier.FromWindowTitle(mediaTitle, title) == MediaWebsite.YouTube
            || string.Equals(mediaTitle.Trim().Normalize(), title.Trim().Normalize(), StringComparison.OrdinalIgnoreCase));
    private static string Name(IUIAutomationElement element) { element.GetCurrentPropertyValue(30005, out var name); return name as string ?? ""; }
    private static bool Selected(IUIAutomationElement element) { element.GetCurrentPropertyValue(30079, out var selected); return selected is true; }
    private static string? VideoId(IUIAutomationElement element, bool includeDescriptions = false)
    {
        foreach (var property in includeDescriptions ? new[] { 30045, 30093, 30013, 30159 } : new[] { 30045, 30093 })
        {
            element.GetCurrentPropertyValue(property, out var value);
            if (value is string url && YouTubeVideo.IdFromUrl(url) is { } id) return id;
        }
        return null;
    }

    // Slots follow the Windows SDK UIAutomationClient.h vtable. Unused slots
    // keep the ABI stable without loading the WPF automation assemblies.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        void ElementFromHandle(IntPtr window, out IUIAutomationElement element);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void GetControlViewWalker(); void GetContentViewWalker(); void GetRawViewWalker();
        void GetRawViewCondition(); void GetControlViewCondition(); void GetContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(out IUIAutomationCondition condition); void CreateFalseCondition();
        void CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
        void CreatePropertyConditionEx(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, int flags, out IUIAutomationCondition condition);
        void CreateAndCondition(IUIAutomationCondition first, IUIAutomationCondition second, out IUIAutomationCondition condition);
        void CreateAndConditionFromArray(); void CreateAndConditionFromNativeArray();
        void CreateOrCondition(IUIAutomationCondition first, IUIAutomationCondition second, out IUIAutomationCondition condition);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId();
        void FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? element);
        void FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray elements);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
        void GetCurrentPatternAs(); void GetCachedPatternAs();
        void GetCurrentPattern(int pattern, out IUIAutomationSelectionItemPattern value);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        void GetLength(out int length);
        void GetElement(int index, out IUIAutomationElement element);
    }

    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationSelectionItemPattern { void Select(); }
}
