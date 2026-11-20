using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using System.Threading;

namespace LocalMessaging.WinUI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        NativeOptions options;
        try { options = NativeOptions.Parse(args); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new MessagingApp(options);
        });
        return 0;
    }
}

// The XAML compiler normally generates an IXamlMetadataProvider from App.xaml/Page items.
// This code-only project has none (microsoft-ui-xaml#7606), so the Application supplies
// the WinUI framework provider itself; without it XAML resource loading fails at startup.
internal sealed class MessagingApp : Application, IXamlMetadataProvider
{
    private Window? window;
    private readonly NativeOptions options;
    private readonly XamlControlsXamlMetaDataProvider frameworkMetadata = new();
    public MessagingApp(NativeOptions options)
    {
        this.options = options;
    }
    public IXamlType? GetXamlType(Type type) => frameworkMetadata.GetXamlType(type);
    public IXamlType? GetXamlType(string fullName) => frameworkMetadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => frameworkMetadata.GetXmlnsDefinitions();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // WinUI 3 quirk (microsoft-ui-xaml#4639): Application.Resources throws 0x8000FFFF
        // when touched in the constructor; resource initialization must happen here.
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        window = new MainWindow(options);
        window.Activate();
    }
}
