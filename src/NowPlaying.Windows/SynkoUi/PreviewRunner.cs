using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace NowPlaying.Windows.SynkoUi;

internal static class PreviewRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Run(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var exitCode = 0;
        using var primary = new SynkoWindow("light");
        primary.Shown += async (_, _) =>
        {
            try
            {
                await primary.Ready.WaitAsync(TimeSpan.FromSeconds(25));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var captured = await PreviewSnapshot.CaptureAsync(timeout.Token);
                var snapshot = captured with
                {
                    Enabled = true,
                    Media = captured.Media is null ? null : captured.Media with { Playing = true, ObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    Sources = captured.Sources.Select(source => source with
                    {
                        Playing = captured.Media is not null && (captured.Media.Source == "apple" ? source.Id == "apple" : source.Browser == captured.Media.Browser)
                    }).ToArray()
                };
                var reports = new List<object>();
                reports.Add(new { components = await CheckComponentsAsync(primary) });
                foreach (var (theme, scale) in new[] { ("light", 1d), ("dark", 1d), ("light", 1.5), ("dark", 1.5) })
                {
                    var form = theme == "light" && scale == 1 ? primary : new SynkoWindow(theme, scale);
                    try
                    {
                        if (form != primary) { primary.Hide(); form.Show(); await form.Ready.WaitAsync(TimeSpan.FromSeconds(25)); }
                        form.Show();
                        form.TopMost = true;
                        form.Activate();
                        form.Send(new { type = "visibility", visible = true });
                        form.Send(new { type = "state", state = snapshot });
                        await WaitAsync(form, "window.synko.diagnostics().state !== null && [...document.querySelectorAll('.app-shell > *')].every(e=>getComputedStyle(e).opacity==='1')");
                        await WaitAsync(form, "!document.querySelector('.cover img') || (document.querySelector('.cover img').complete && document.querySelector('.cover img').naturalWidth > 0)");
                        await Task.Delay(700);
                        var slug = theme + "-" + (int)(scale * 100);
                        await using (var output = File.Create(Path.Combine(outputFolder, slug + ".png")))
                            await form.Web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                        form.TopMost = true;
                        form.Activate();
                        await Task.Delay(80);
                        using (var window = new Bitmap(form.Width, form.Height))
                        {
                            using var graphics = Graphics.FromImage(window);
                            graphics.CopyFromScreen(form.Location, Point.Empty, window.Size);
                            window.Save(Path.Combine(outputFolder, slug + "-window.png"), ImageFormat.Png);
                        }
                        form.TopMost = false;
                        var report = await CheckMainAsync(form, scale);
                        reports.Add(new { theme, scale, checks = report });
                        var statuses = await CheckSourceStatusesAsync(form, snapshot, outputFolder, slug);
                        reports.Add(new { theme, scale, statuses });
                        var spotify = await CheckSpotifyAsync(form, snapshot, outputFolder, slug);
                        reports.Add(new { theme, scale, spotify });
                        form.Send(new { type = "state", state = snapshot with { Preferences = LiveSnapshot.Preferences(SettingsStore.Load() with { OnboardingDone = true, ThemeMode = "system", OnboardingStep = 0 }) } });
                        await CaptureAdditionalAsync(form, outputFolder, slug);
                    }
                    finally { if (form != primary) { form.ExitPreview(); form.Dispose(); } }
                }
                await File.WriteAllTextAsync(Path.Combine(outputFolder, "checks.json"), JsonSerializer.Serialize(reports, Json));
            }
            catch (Exception ex)
            {
                exitCode = 1;
                var detail = ex.ToString();
                try { detail += "\n" + await primary.Web.ExecuteScriptAsync("JSON.stringify({url:location.href,ready:document.readyState,hidden:document.hidden,focused:document.hasFocus(),animationCount:window.synko?.diagnostics().animationCount,coverDecoded:document.querySelector('.cover img')?.naturalWidth,opacity:[...document.querySelectorAll('.app-shell > *')].map(e=>getComputedStyle(e).opacity)})"); }
                catch (Exception browserError) { detail += "\n" + browserError.Message; }
                await File.WriteAllTextAsync(Path.Combine(outputFolder, "error.txt"), detail);
            }
            finally { primary.ExitPreview(); }
        };
        Application.Run(primary);
        return exitCode;
    }

    private static async Task<JsonElement> CheckComponentsAsync(SynkoWindow form)
    {
        var result = await EvaluateAsync(form, """
            (()=>{
              window.synko.showView('components');
              const toggle=document.querySelector('[role="switch"]');
              const before=toggle.getAttribute('aria-checked');toggle.click();
              const toggles=toggle.getAttribute('aria-checked')!==before&&document.getElementById('app').dataset.enabled==='false';
              const radios=[...document.querySelectorAll('[role="radio"]')];radios[2].click();
              const selects=radios[2].getAttribute('aria-checked')==='true'&&radios[0].getAttribute('aria-checked')==='false'&&document.getElementById('app').dataset.source==='browser';
              const input=document.querySelector('input');input.focus();
              const focus=document.activeElement===input&&getComputedStyle(input).outlineWidth==='3px';
              document.querySelector('button[aria-label="Настройки"]').click();
              const dialog=document.querySelector('dialog');const opens=dialog.open;
              dialog.querySelector('button').click();
              const closes=!dialog.open;
              const result={toggles,selects,focus,opens,closes,fonts:document.fonts.check('400 16px "Roboto Flex"')&&document.fonts.check('400 24px "Material Symbols Rounded"')};
              window.synko.showView('main');
              return JSON.stringify(result);
            })()
            """);
        RequireAll(result);
        return result;
    }

    private static async Task<JsonElement> CheckMainAsync(SynkoWindow form, double scale)
    {
        var result = await EvaluateAsync(form, """
            (()=>{
              const s=window.synko.diagnostics();
              const canvas=document.querySelector('canvas');
              const bounds=[...document.querySelectorAll('.player-card,.publication-card,.source-row')].filter(e=>!e.hidden).map(e=>e.getBoundingClientRect());
              const aligned=bounds.every(b=>Math.abs(b.left-bounds[0].left)<.5&&Math.abs(b.right-bounds[0].right)<.5);
              const rgb=h=>[1,3,5].map(i=>parseInt(h.slice(i,i+2),16)/255).map(v=>v<=.04045?v/12.92:Math.pow((v+.055)/1.055,2.4));
              const luminance=h=>rgb(h).reduce((sum,v,i)=>sum+v*[.2126,.7152,.0722][i],0);
              const ratio=(a,b)=>{const x=luminance(a),y=luminance(b);return(Math.max(x,y)+.05)/(Math.min(x,y)+.05)};
              const p=s.palette;
              const contrast=ratio(p.onSurface,p.surface)>=4.5&&ratio(p.onSurfaceVariant,p.surface)>=4.5&&ratio(p.onPrimaryContainer,p.primaryContainer)>=4.5&&ratio(p.onSurfaceVariant,p.primaryContainer)>=4.5;
              return JSON.stringify({aligned,contrast,width:s.width,dpr:s.dpr,canvas:canvas.width===Math.round(canvas.clientWidth*devicePixelRatio)&&canvas.height===Math.round(24*devicePixelRatio),noPlayerControls:document.querySelectorAll('input[type="range"]').length===0,noConnectionIndicator:!document.querySelector('.topbar').textContent.includes('Подключено'),localFonts:document.fonts.status==='loaded'});
            })()
            """);
        foreach (var value in result.EnumerateObject())
            if (value.Value.ValueKind == JsonValueKind.False) throw new InvalidOperationException("UI check failed: " + value.Name);
        if (Math.Abs(result.GetProperty("dpr").GetDouble() - scale) > .01 || result.GetProperty("width").GetInt32() != 440)
            throw new InvalidOperationException("Unexpected viewport scale.");
        await form.Web.ExecuteScriptAsync("window.synko.receive({type:'visibility',visible:false})");
        await Task.Delay(80);
        var before = await ReadFramesAsync(form);
        await Task.Delay(100);
        var after = await ReadFramesAsync(form);
        if (before != after) throw new InvalidOperationException("Wave keeps drawing while hidden.");
        await form.Web.ExecuteScriptAsync("window.synko.receive({type:'visibility',visible:true});const paused=structuredClone(window.synko.diagnostics().state);if(paused.media){paused.media.playing=false;paused.media.observedAt=Date.now();}window.synko.receive({type:'state',state:paused});");
        await Task.Delay(1000);
        var paused = await EvaluateAsync(form, "JSON.stringify(window.synko.diagnostics().wave)");
        if (Math.Abs(paused.GetProperty("amplitude").GetDouble()) > .02) throw new InvalidOperationException("Wave did not flatten on pause.");
        var dark = await form.Web.ExecuteScriptAsync("document.documentElement.dataset.dark") == "\"true\"";
        var timeOrigin = await form.Web.ExecuteScriptAsync("performance.timeOrigin");
        form.Send(new { type = "environment", environment = new { dark = !dark } });
        await WaitAsync(form, "document.documentElement.dataset.dark === '" + (!dark ? "true" : "false") + "'");
        if (timeOrigin != await form.Web.ExecuteScriptAsync("performance.timeOrigin")) throw new InvalidOperationException("Theme change reloaded the UI.");
        form.Send(new { type = "environment", environment = new { dark } });
        await WaitAsync(form, "document.documentElement.dataset.dark === '" + (dark ? "true" : "false") + "'");
        return result;
    }

    private static async Task<int> ReadFramesAsync(SynkoWindow form)
        => int.Parse(await form.Web.ExecuteScriptAsync("window.synko.diagnostics().wave.frames"), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonElement> CheckSpotifyAsync(SynkoWindow form, UiSnapshot snapshot, string folder, string slug)
    {
        var preferences = LiveSnapshot.Preferences(SettingsStore.Load() with { SourceMode = Core.SourceMode.Spotify, OnboardingDone = true });
        var fixture = snapshot with
        {
            Media = new UiMedia("spotify-ui-test", "Проверка Spotify", "Тестовая медиасессия", "", "spotify", "Spotify", true,
                42, 180, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null),
            Sources = [new("spotify", "Spotify", true), new("apple", "Apple Music", false)],
            Preferences = preferences
        };
        form.Send(new { type = "state", state = fixture });
        await WaitAsync(form, "window.synko.diagnostics().state.media?.source === 'spotify' && document.querySelector('.source-badge .spotify') !== null");
        await Task.Delay(300);
        await using (var output = File.Create(Path.Combine(folder, slug + "-spotify.png")))
            await form.Web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        var result = await EvaluateAsync(form, """
            (()=>{
              const state=window.synko.diagnostics().state;
              const spotify=document.querySelector('.source-status .spotify');
              const source=spotify?.parentElement;
              const icon=getComputedStyle(document.querySelector('.source-badge .spotify')).maskImage;
              return JSON.stringify({spotifyName:state.media.sourceLabel==='Spotify'&&document.querySelector('.source-badge').textContent==='Spotify',
                spotifyIcons:!!spotify&&!!document.querySelector('.cover .spotify')&&icon.includes('spotify.svg'),
                correctMode:state.preferences.sourceMode===4,
                quietStatus:source?.tagName==='DIV'&&source.querySelector('.source-name').textContent==='Spotify'&&source.dataset.playing==='true',
                noOverflow:document.documentElement.scrollWidth===innerWidth});
            })()
            """);
        RequireAll(result);
        await form.Web.ExecuteScriptAsync("window.synko.showView('settings')");
        var fits = await form.Web.ExecuteScriptAsync("(()=>{const buttons=[...document.querySelectorAll('.settings-section .segment[data-value]')].filter(b=>['0','1','2','4'].includes(b.dataset.value));return buttons.length===4&&buttons.every(button=>{button.click();return buttons.every(b=>b.scrollWidth<=b.clientWidth&&b.scrollHeight<=b.clientHeight)})})()");
        if (fits != "true") throw new InvalidOperationException("Music source choices wrap or overflow in settings.");
        await form.Web.ExecuteScriptAsync("window.synko.showView('main')");
        form.Send(new { type = "state", state = snapshot with { Preferences = LiveSnapshot.Preferences(SettingsStore.Load() with { OnboardingDone = true }) } });
        return result;
    }

    private static async Task<JsonElement> CheckSourceStatusesAsync(SynkoWindow form, UiSnapshot snapshot, string folder, string slug)
    {
        form.Send(new { type = "state", state = snapshot with { Sources = [new("apple", "Apple Music", true), new("browser:Google Chrome", "Chrome", false, "Google Chrome")] } });
        await WaitAsync(form, "document.querySelectorAll('.source-status').length === 2");
        await Task.Delay(250);
        await using (var output = File.Create(Path.Combine(folder, slug + "-sources.png")))
            await form.Web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        var result = await EvaluateAsync(form, """
            (()=>{
              const row=document.querySelector('.source-row'), pills=[...row.children], bounds=row.getBoundingClientRect();
              const left=pills[0].getBoundingClientRect(), right=pills[1].getBoundingClientRect();
              const saved=structuredClone(window.synko.diagnostics().state);
              let noSquare=true;
              for(const position of [0,50,100]){
                const state=structuredClone(saved);
                state.media={...(state.media||{}),key:'progress-check-'+position,position,duration:100,rate:1,observedAt:Date.now(),playing:false};
                window.synko.receive({type:'state',state});
                const canvas=document.querySelector('canvas'), context=canvas.getContext('2d'), y=Math.round(4*devicePixelRatio);
                const strip=context.getImageData(0,y,canvas.width,1).data;
                if(strip.some((value,index)=>index%4===3&&value>0))noSquare=false;
              }
              window.synko.receive({type:'state',state:saved});
              return JSON.stringify({twoSides:Math.abs(left.left-bounds.left)<1&&Math.abs(right.right-bounds.right)<1&&right.left>left.right,
                nonInteractive:pills.every(e=>!e.matches('button,[role="button"]')&&e.tabIndex<0&&getComputedStyle(e).cursor==='default'),
                separateLabels:pills.every(e=>e.querySelector('.source-name')&&e.querySelector('.source-playback')),
                insideWindow:left.width>0&&right.right<=innerWidth,rounded:pills.every(e=>parseFloat(getComputedStyle(e).borderRadius)>=16),noSquare});
            })()
            """);
        RequireAll(result);
        form.Send(new { type = "state", state = snapshot });
        return result;
    }

    private static async Task CaptureAdditionalAsync(SynkoWindow form, string folder, string slug)
    {
        await form.Web.ExecuteScriptAsync("window.synko.showView('settings')");
        await Task.Delay(600);
        var result = await form.Web.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"captureBeyondViewport\":true,\"clip\":{\"x\":0,\"y\":0,\"width\":440,\"height\":" + await form.Web.ExecuteScriptAsync("document.documentElement.scrollHeight") + ",\"scale\":1}}");
        using (var document = JsonDocument.Parse(result))
            await File.WriteAllBytesAsync(Path.Combine(folder, slug + "-settings.png"), Convert.FromBase64String(document.RootElement.GetProperty("data").GetString()!));
        await form.Web.ExecuteScriptAsync("window.synko.showView('onboarding')");
        for (var step = 0; step < 5; step++)
        {
            await Task.Delay(500);
            var expected = new[] { "Synko", "Подключите Discord", "Откуда брать музыку", "Внешний вид", "Всё готово" }[step];
            if (JsonSerializer.Deserialize<string>(await form.Web.ExecuteScriptAsync("document.querySelector('h1').textContent")) != expected)
                throw new InvalidOperationException("Onboarding did not reach step " + step);
            await using (var output = File.Create(Path.Combine(folder, slug + "-onboarding-" + step + ".png")))
                await form.Web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            if (step < 4) await form.Web.ExecuteScriptAsync("(()=>{const next=document.querySelector('.setup-footer > :last-child');next.disabled=false;next.click()})()");
        }
    }

    private static async Task<JsonElement> EvaluateAsync(SynkoWindow form, string script)
    {
        var result = await form.Web.ExecuteScriptAsync(script);
        var json = JsonSerializer.Deserialize<string>(result) ?? throw new InvalidOperationException("Browser returned no result.");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void RequireAll(JsonElement result)
    {
        foreach (var value in result.EnumerateObject()) if (value.Value.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Component check failed: " + value.Name);
    }

    private static async Task WaitAsync(SynkoWindow form, string predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (await form.Web.ExecuteScriptAsync(predicate) == "true") return;
            await Task.Delay(40);
        }
        throw new TimeoutException("UI condition timed out: " + predicate);
    }
}
