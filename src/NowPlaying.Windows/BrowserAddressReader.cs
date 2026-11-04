using System.Runtime.InteropServices;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal static class BrowserAddressReader
{
    internal sealed record Result(MediaWebsite Website, string? VideoId);
    internal static string? LastDiagnostic { get; private set; }
    private static readonly string[] AddressBarNames =
    [
        "Address and search bar", "Адресная строка и строка поиска", "Адресная строка",
        "Search or enter address", "Поиск или ввод адреса", "Search with Google or enter address"
    ];

    public static Result Read(IntPtr window, string mediaTitle, bool probeAddress = false)
    {
        var activeYouTube = BrowserSiteClassifier.FromWindowTitle(mediaTitle, BrowserWebsiteDetector.WindowTitle(window)) == MediaWebsite.YouTube;
        LastDiagnostic = "Reading current browser accessibility tree; activeYouTube=" + activeYouTube;
        var references = new List<object>();
        try
        {
            var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("ff48dba4-60ef-4201-aa87-54103eef594e"), true)!)!;
            references.Add(automation);
            automation.ElementFromHandle(window, out var root); references.Add(root);
            if (!activeYouTube)
            {
                automation.CreatePropertyCondition(30003, 50019, out var tabCondition); references.Add(tabCondition);
                root.FindAll(4, tabCondition, out var tabs); references.Add(tabs);
                tabs.GetLength(out var count);
                LastDiagnostic = "Open tab count=" + count;
                for (var index = 0; index < Math.Min(count, 256); index++)
                {
                    tabs.GetElement(index, out var tab); references.Add(tab);
                    tab.GetCurrentPropertyValue(30005, out var name);
                    if (name is string title && BrowserSiteClassifier.FromWindowTitle(mediaTitle, title) == MediaWebsite.YouTube)
                    {
                        if (probeAddress)
                        {
                            automation.CreatePropertyCondition(30079, true, out var selectedCondition); references.Add(selectedCondition);
                            automation.CreateAndCondition(tabCondition, selectedCondition, out var selectedTabCondition); references.Add(selectedTabCondition);
                            root.FindFirst(4, selectedTabCondition, out var originalTab);
                            if (originalTab is not null)
                            {
                                references.Add(originalTab);
                                originalTab.GetCurrentPattern(10010, out var originalSelection); references.Add(originalSelection);
                                tab.GetCurrentPattern(10010, out var selection); references.Add(selection);
                                try { selection.Select(); Thread.Sleep(250); return Read(window, mediaTitle); }
                                finally { originalSelection.Select(); LastDiagnostic = "Probed current media address and restored the original tab"; }
                            }
                        }
                        foreach (var property in new[] { 30045, 30093, 30013, 30159 })
                        {
                            tab.GetCurrentPropertyValue(property, out var detail);
                            if (detail is string url && YouTubeVideo.IdFromUrl(url) is { } tabVideoId)
                            { LastDiagnostic = "Matched a background YouTube media tab and its URL"; return new(MediaWebsite.YouTube, tabVideoId); }
                        }
                        LastDiagnostic = "Matched a background YouTube media tab; thumbnail comes from Windows media";
                        return new(MediaWebsite.YouTube, null);
                    }
                }
                return new(MediaWebsite.Unknown, null);
            }
            IUIAutomationCondition? names = null;
            foreach (var (property, value) in AddressBarNames.Select(name => (30005, name)).Concat([(30011, "urlbar-input"), (30011, "urlbar")]))
            {
                automation.CreatePropertyConditionEx(property, value, 1, out var condition); references.Add(condition);
                if (names is null) names = condition;
                else { automation.CreateOrCondition(names, condition, out var combined); names = combined; references.Add(combined); }
            }
            automation.CreatePropertyCondition(30003, 50004, out var edit); references.Add(edit);
            automation.CreateAndCondition(edit, names!, out var addressCondition); references.Add(addressCondition);
            root.FindFirst(4, addressCondition, out var address);
            if (address is null) return new(MediaWebsite.YouTube, null);
            references.Add(address);
            address.GetCurrentPropertyValue(30008, out var focused);
            if (focused is true) return new(MediaWebsite.YouTube, null);
            address.GetCurrentPropertyValue(30045, out var valueInAddressBar);
            var id = YouTubeVideo.IdFromUrl(valueInAddressBar as string ?? "");
            return BrowserSiteClassifier.FromWindowTitle(mediaTitle, BrowserWebsiteDetector.WindowTitle(window)) == MediaWebsite.YouTube
                ? new(MediaWebsite.YouTube, id) : new(MediaWebsite.Unknown, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { LastDiagnostic = ex.GetType().Name + ": " + ex.Message; return new(activeYouTube ? MediaWebsite.YouTube : MediaWebsite.Unknown, null); }
        finally
        {
            foreach (var reference in references.Distinct(ReferenceEqualityComparer.Instance).Reverse())
                if (Marshal.IsComObject(reference)) Marshal.ReleaseComObject(reference);
        }
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
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
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
