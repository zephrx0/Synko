using System.Text.Json;
using NowPlaying.Core;
using Microsoft.Web.WebView2.Core;

namespace NowPlaying.Windows.SynkoUi;

internal static class SmokeRunner
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = SettingsStore.Load() with { ApplicationId = "123456789012345678", Enabled = false, OnboardingDone = true, ThemeMode = "system", AutoCheckUpdates = false, QuietStartup = true, SourceMode = NowPlaying.Core.SourceMode.Automatic };
        SettingsStore.OverrideFolder = Path.Combine(folder, "isolated-profile");
        SettingsStore.Save(settings);
        var testRegistry = @"Software\Synko.Tests\" + Guid.NewGuid().ToString("N");
        StartupRegistration.OverrideRegistryPath = testRegistry;
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        string? openedUrl = null;
        using var context = new TrayApplication(settings, signal, background: true, readClipboard: () => "abc123", openUrl: url => openedUrl = url);
        var window = context.Window;
        // Keep desktop input from changing the screen during scripted checks.
        // DOM actions still run through the real WebView bridge.
        window.Enabled = false;
        var exitCode = 0;
        window.Shown += async (_, _) =>
        {
            try
            {
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(30));
                await WaitAsync(window, "window.synko.diagnostics().state?.preferences?.onboardingDone === true");
                await Task.Delay(4000);
                Require(await Script(window, "[...document.querySelectorAll('.cover img')].every(image=>image.complete && image.naturalWidth>0)"), "Artwork bytes did not decode in WebView");
                await CaptureAsync(window, folder, "production-home");
                Require(Application.OpenForms.Cast<Form>().All(f => f is SynkoWindow), "Unexpected window was created");
                Require(await Script(window, "document.querySelectorAll('button').length === 2"), "Home has extra buttons");
                Require(await Script(window, "!document.querySelector('.source-row').textContent.includes('—') && !document.querySelector('.source-row').textContent.includes('·')"), "Source labels contain unwanted punctuation");
                await window.Web.ExecuteScriptAsync("(async()=>{const {symbol}=await import('./components/interaction.js');const {browserIcon}=await import('./brand.js');const box=document.createElement('div');box.hidden=true;document.body.append(box);for(const name of ['Google Chrome','Microsoft Edge','Firefox','Brave','Vivaldi','Opera','Opera GX','Arc','Яндекс Браузер','Chromium','Thorium','LibreWolf','Waterfox','Floorp','Zen','Helium'])box.append(symbol(browserIcon(name)));try{await Promise.all([...box.querySelectorAll('img')].map(image=>image.decode()));window.browserIconsVerified=box.querySelectorAll('img').length===16&&[...box.querySelectorAll('img')].every(image=>image.naturalWidth>0)}catch{window.browserIconsVerified=false}finally{box.remove()}})()");
                await WaitAsync(window, "window.browserIconsVerified === true");
                await window.Web.ExecuteScriptAsync("document.querySelector('button[aria-label=\"Настройки\"]').click()");
                await WaitAsync(window, "window.synko.diagnostics().view === 'settings' && document.querySelector('.settings-section input') !== null");
                await CaptureAsync(window, folder, "production-settings");
                await window.Web.ExecuteScriptAsync("document.querySelector('.segment[data-value=\"4\"]').click()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.sourceMode === 4");
                Require(SettingsStore.Load().SourceMode == SourceMode.Spotify, "Spotify source did not persist");
                await window.Web.ExecuteScriptAsync("document.querySelector('.segment[data-value=\"0\"]').click()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.sourceMode === 0");
                await window.Web.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent==='Вставить').click()");
                await WaitAsync(window, "document.querySelector('input').value === '123'");
                await window.Web.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent.includes('Developer Portal')).click()");
                Require(openedUrl == "https://discord.com/developers/applications", "Developer Portal URL is incorrect");
                await window.Web.ExecuteScriptAsync("(()=>{const rows=[...document.querySelectorAll('.settings-row')];rows.find(r=>r.textContent.includes('Запускать вместе')).querySelector('button').click()})()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.startup === true");
                Require(StartupRegistration.IsEnabled(), "Startup did not save to the isolated registry key");
                Require(await Script(window, "[...document.querySelectorAll('.settings-row')].find(r=>r.querySelector('.row-label')?.textContent==='Тихий запуск').querySelector('[role=switch]').getAttribute('aria-checked') === 'true'"), "Quiet startup should be enabled by default");
                await window.Web.ExecuteScriptAsync("[...document.querySelectorAll('.settings-row')].find(r=>r.querySelector('.row-label')?.textContent==='Тихий запуск').querySelector('[role=switch]').click()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.quietStartup === false");
                Require(!SettingsStore.Load().QuietStartup && StartupRegistration.IsEnabled(), "Quiet opt-out must persist independently of autostart");
                Require(WindowsRestart.ReadArguments() == "--startup", "Quiet opt-out must immediately update Windows restoration");
                await window.Web.ExecuteScriptAsync("[...document.querySelectorAll('.settings-row')].find(r=>r.querySelector('.row-label')?.textContent==='Тихий запуск').querySelector('[role=switch]').click()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.quietStartup === true");
                Require(SettingsStore.Load().QuietStartup, "Quiet startup re-enabling must persist");
                Require(WindowsRestart.ReadArguments() == "--startup --background", "Quiet switch must immediately update Windows restoration");
                await window.Web.ExecuteScriptAsync("(()=>{const rows=[...document.querySelectorAll('.settings-row')];rows.find(r=>r.textContent.includes('Запускать вместе')).querySelector('button').click()})()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.startup === false");
                foreach (var option in new[] { ("Обложки музыки", "automaticArtwork"), ("Превью YouTube", "youTubeThumbnails"), ("Проверять при запуске", "autoCheckUpdates") })
                {
                    var text = JsonSerializer.Serialize(option.Item1);
                    await window.Web.ExecuteScriptAsync("(()=>{const row=[...document.querySelectorAll('.settings-row')].find(r=>r.querySelector('.row-label').textContent===" + text + ");row.querySelector('[role=switch]').click()})()");
                    await Task.Delay(180);
                    var changed = SettingsStore.Load();
                    var actual = option.Item2 switch { "automaticArtwork" => changed.AutomaticArtwork, "youTubeThumbnails" => changed.YouTubeThumbnails, _ => changed.AutoCheckUpdates };
                    Require(actual != (option.Item2 == "autoCheckUpdates" ? settings.AutoCheckUpdates : true), "Option did not persist: " + option.Item2);
                    await window.Web.ExecuteScriptAsync("(()=>{const row=[...document.querySelectorAll('.settings-row')].find(r=>r.querySelector('.row-label').textContent===" + text + ");row.querySelector('[role=switch]').click()})()");
                    await Task.Delay(100);
                }
                await window.Web.ExecuteScriptAsync("document.querySelector('.color-swatch[data-seed=\"#426894\"]').click()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.customSeed === '#426894'");
                Require(SettingsStore.Load().ColorSource == "custom", "Color choice did not persist");
                await window.Web.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent==='Проверить обновления').click()");
                await WaitAsync(window, "!['idle','checking'].includes(window.synko.diagnostics().state.update.status)");
                var updateCheckStatus = JsonSerializer.Deserialize<string>(await window.Web.ExecuteScriptAsync("window.synko.diagnostics().state.update.status"));
                await window.Web.ExecuteScriptAsync("document.querySelector('button[role=radio][data-value=light]').click()");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.themeMode === 'light'");
                await Task.Delay(250);
                Require(SettingsStore.Load().ThemeMode == "light", "Theme change did not persist");
                await window.Web.ExecuteScriptAsync("const fallback=document.querySelectorAll('input')[1];fallback.value='smoke-artwork';fallback.dispatchEvent(new Event('change',{bubbles:true}))");
                await WaitAsync(window, "window.synko.diagnostics().state.preferences.imageKey === 'smoke-artwork'");
                Require(SettingsStore.Load().ImageKey == "smoke-artwork", "Artwork key did not persist");
                await window.Web.ExecuteScriptAsync("const input=document.querySelector('input');input.value='123';input.dispatchEvent(new Event('input',{bubbles:true}))");
                Require(await Script(window, "document.querySelector('input').getAttribute('aria-invalid') === 'true'"), "Invalid ID was not rejected");
                Require(SettingsStore.Load().ApplicationId == settings.ApplicationId, "Invalid ID overwrote saved ID");
                await window.Web.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(b=>b.textContent==='Пройти настройку заново').click()");
                await WaitAsync(window, "window.synko.diagnostics().view === 'onboarding'");
                await CaptureAsync(window, folder, "production-onboarding");
                await window.Web.ExecuteScriptAsync("document.querySelector('.setup-footer button').click()");
                await WaitAsync(window, "document.querySelector('h1').textContent === 'Подключите Discord'");
                await CaptureAsync(window, folder, "production-onboarding-discord");
                await window.Web.ExecuteScriptAsync("document.querySelector('.setup-footer > :last-child').click()");
                await WaitAsync(window, "document.querySelector('h1').textContent === 'Откуда брать музыку'");
                await window.Web.ExecuteScriptAsync("document.querySelector('.source-choice[data-mode=\"0\"]').click()");
                await Task.Delay(150);
                Require(SettingsStore.Load().SourceMode == NowPlaying.Core.SourceMode.Automatic, "Source mode did not persist");
                await window.Web.ExecuteScriptAsync("document.querySelector('.setup-footer > :last-child').click()");
                await WaitAsync(window, "document.querySelector('h1').textContent === 'Внешний вид' && document.querySelector('.color-swatches') !== null");
                await CaptureAsync(window, folder, "production-onboarding-appearance");
                await window.Web.ExecuteScriptAsync("document.querySelector('.setup-footer > :last-child').click()");
                await WaitAsync(window, "document.querySelector('h1').textContent === 'Всё готово'");
                await window.Web.ExecuteScriptAsync("document.querySelector('.setup-footer > :last-child').click()");
                await WaitAsync(window, "window.synko.diagnostics().view === 'main'");
                Require(SettingsStore.Load().OnboardingDone, "Wizard completion did not persist");
                Require(Application.OpenForms.Cast<Form>().All(f => f is SynkoWindow), "Settings opened another window");
                window.Hide(); await Task.Delay(150); Require(!window.IsDisposed, "Hide disposed the window");
                window.Show(); await Task.Delay(250); Require(window.Visible, "Window did not reopen");
                await File.WriteAllTextAsync(Path.Combine(folder, "production-checks.json"), JsonSerializer.Serialize(new
                {
                    success = true, executable = Environment.ProcessPath, version = Application.ProductVersion,
                    newShell = true, noLegacyWindow = true, homeButtons = 2, settingsNavigateInPlace = true,
                    themeSaved = true, artworkKeySaved = true, invalidIdPreservesSettings = true, spotifySourceSaved = true,
                    onboardingOpensInPlace = true, allFiveWizardSteps = true, sourceSaved = true, wizardCompletionSaved = true,
                    hideAndReopen = true, userSettingsUntouched = true,
                    publicationDisabledDuringCheck = true, browserIcons = 16, noLongDashes = true, pasteBridge = true,
                    portalBridge = true, startupIsolatedRegistry = true, artworkAndUpdateToggles = true, customColorSaved = true,
                    quietStartupDefaultAndPersistence = true,
                    nativeQuietRestartCommand = true,
                    updateCheckStatus
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                exitCode = 1;
                await File.WriteAllTextAsync(Path.Combine(folder, "production-error.txt"), ex.ToString());
            }
            finally { await context.ExitAsync(); }
        };
        window.Show();
        Application.Run(context);
        SettingsStore.OverrideFolder = null;
        StartupRegistration.OverrideRegistryPath = null;
        using (var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Synko.Tests", writable: true))
            registry?.DeleteSubKeyTree(testRegistry.Split('\\')[^1], throwOnMissingSubKey: false);
        return exitCode;
    }

    private static async Task<bool> Script(SynkoWindow window, string predicate) => await window.Web.ExecuteScriptAsync(predicate) == "true";
    private static async Task WaitAsync(SynkoWindow window, string predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until) { if (await Script(window, predicate)) return; await Task.Delay(50); }
        throw new TimeoutException(predicate);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task CaptureAsync(SynkoWindow window, string folder, string name)
    {
        await Task.Delay(500);
        await using (var file = File.Create(Path.Combine(folder, name + ".png")))
            await window.Web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }
}
